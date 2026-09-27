const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');

const root = __dirname;
const version = '0.3.0-test';
const luaType = 0xA14E8DFA2CD117E2n;
const mask = 0xffffffffffffffffn;
const mix = 0xC6A4A7935BD1E995n;
const resource = 'mods/hd2_helper/auto_reload';
const sourceFolder = process.argv[2];
const readSource = file => fs.readFileSync(file, 'utf8').replace(/\r\n/g, '\n');

function extract(relative, expectedHash) {
  const archive = fs.readFileSync(path.join(sourceFolder, relative));
  assert.equal(crypto.createHash('sha256').update(archive).digest('hex'), expectedHash);
  assert.equal(archive.readUInt32LE(0), 0xf0000011);
  assert.equal(archive.readUInt32LE(8), 1);
  const entry = 72 + 32 * archive.readUInt32LE(4);
  assert.equal(archive.readBigUInt64LE(entry + 8), luaType);
  const offset = Number(archive.readBigUInt64LE(entry + 16));
  const length = archive.readUInt32LE(entry + 56);
  assert.equal(archive.readUInt32LE(offset + 4), 2);
  return archive.subarray(offset + 8, offset + length).toString('utf8');
}
function hash64(value) {
  const data = Buffer.from(value);
  let h = (BigInt(data.length) * mix) & mask;
  let i = 0;
  while (i + 8 <= data.length) {
    let k = (data.readBigUInt64LE(i) * mix) & mask;
    k ^= k >> 47n;
    k = (k * mix) & mask;
    h = ((h ^ k) * mix) & mask;
    i += 8;
  }
  if (i < data.length) {
    let tail = 0n;
    for (let j = i; j < data.length; j++) tail |= BigInt(data[j]) << BigInt((j - i) * 8);
    h = ((h ^ tail) * mix) & mask;
  }
  h ^= h >> 47n;
  h = (h * mix) & mask;
  return h ^ (h >> 47n);
}
const common = sourceFolder && extract('COMMON/9ba626afa44a3aa3.patch_0',
  'f4736d958f0228ab955dcb20cdfd7be65d8089df672b0ec8524e765d515ca8c5');
let numbers = sourceFolder && extract('NUMBERS_BOTH/9ba626afa44a3aa3.patch_4',
  '9c25cc086aeea9cef03d9be8c0c14e352d156d6f68c5ab5cae161b35b509e420');
const compact = text => text.split('\n').filter(line => line.trim())
  .map(line => line.replace(/[ \t]+$/, '')).join('\n');
const vendor = path.join(root, 'vendor');
let core;
if (sourceFolder) {
const start = common.indexOf('local __module_registry = {}');
const boundary = 'do\nlocal __imports = {}\nlocal __factory = (function()\nreturn function(imports)\n';
const sections = common.slice(start).split(boundary);
const chosen = ['20-identity-core', '30-generated-common', '40-provider'].map(name => {
  const matches = sections.filter(section => section.startsWith(`if type(imports) ~= "table" then error("${name}:`));
  assert.equal(matches.length, 1, `Reader module ${name} boundary changed`);
  return boundary + matches[0];
});
assert(start > 0 && sections[0].startsWith('local __module_registry = {}'));
core = '-- Derived from HD2 HUD+ 0.1.2 by DDRK1NG; see THIRD_PARTY.txt.\n' +
  compact(sections[0] + chosen.join('')) + '\n' +
  'if #__module_errors > 0 then error(table.concat(__module_errors, "; ")) end\nreturn __module_registry\n';
assert(!core.includes('__original_boot') && !core.includes('90-main'));
fs.mkdirSync(vendor, { recursive: true });
fs.writeFileSync(path.join(vendor, 'reader_core.lua'), core);
fs.writeFileSync(path.join(vendor, 'numbers.lua'), compact(numbers));
fs.copyFileSync(path.join(sourceFolder, 'README.txt'), path.join(vendor, 'HD2-HUD-0.1.2-original-README.txt'));
} else {
  core = readSource(path.join(vendor, 'reader_core.lua'));
  numbers = readSource(path.join(vendor, 'numbers.lua'));
}
const source = readSource(path.join(root, 'addon.lua'))
  .replace('-- @POLICY@', () => readSource(path.join(root, 'policy.lua')))
  .replace('-- @READER_CORE@', () => core)
  .replace('-- @NUMBERS@', () => compact(numbers));
assert.equal(source.split('\n')[0], `-- HD2-Addon: ${resource}`);
const stage = path.join(root, 'dist', `HD2-AutoReload-${version}`);
fs.mkdirSync(path.join(stage, 'Addon'), { recursive: true });
fs.writeFileSync(path.join(root, 'dist', 'auto_reload.generated.lua'), source);
fs.writeFileSync(path.join(root, 'dist', 'reader_core.lua'), core);
fs.writeFileSync(path.join(root, 'dist', 'numbers.lua'), compact(numbers));
fs.copyFileSync(path.join(vendor, 'HD2-HUD-0.1.2-original-README.txt'), path.join(stage, 'HD2-HUD-0.1.2-original-README.txt'));
for (const file of ['README.md', 'THIRD_PARTY.txt']) fs.copyFileSync(path.join(root, file), path.join(stage, file));
const lua = Buffer.from(source, 'utf8');
const payload = Buffer.alloc(lua.length + 8);
payload.writeUInt32LE(lua.length, 0);
payload.writeUInt32LE(2, 4);
lua.copy(payload, 8);
const offset = 192;
const archive = Buffer.alloc(offset + Math.ceil(payload.length / 16) * 16);
archive.writeUInt32LE(0xf0000011, 0);
archive.writeUInt32LE(1, 4);
archive.writeUInt32LE(1, 8);
archive.writeBigUInt64LE(BigInt(archive.length), 32);
archive.writeBigUInt64LE(luaType, 80);
archive.writeUInt32LE(1, 88);
archive.writeUInt32LE(16, 96);
archive.writeUInt32LE(16, 100);
archive.writeBigUInt64LE(hash64(resource), 104);
archive.writeBigUInt64LE(luaType, 112);
archive.writeBigUInt64LE(BigInt(offset), 120);
archive.writeUInt32LE(payload.length, 160);
archive.writeUInt32LE(16, 172);
archive.writeUInt32LE(16, 176);
payload.copy(archive, offset);
const filename = '9ba626afa44a3aa3.patch_0';
fs.writeFileSync(path.join(stage, 'Addon', filename), archive);
for (const suffix of ['.stream', '.gpu_resources']) fs.writeFileSync(path.join(stage, 'Addon', filename + suffix), Buffer.alloc(0));
const description = `Auto reload ${version}. Requires Bingus Shared Loader v15+ / API 1. ` +
  'Checks ammunition exhaustion, actual weapon swaps, and fire attempts. Uses a read-only ' +
  'reader derived from HD2 HUD+ 0.1.2 by DDRK1NG. Heat weapons and underbarrels excluded. Live testing required.';
fs.writeFileSync(path.join(stage, 'manifest.json'), JSON.stringify({
  Version: 1, Guid: '9d720fab-718f-4c91-93c5-31c4c3e6c42e', Name: `HD2 Helper Auto Reload ${version}`,
  Description: description, Options: [{ Name: 'Auto Reload', Description: description, Include: ['Addon'] }]
}, null, 2));
const report = { version, resource, resourceHash: hash64(resource).toString(16),
  archiveBytes: archive.length, sourceBytes: lua.length, stage,
  archiveSha256: crypto.createHash('sha256').update(archive).digest('hex') };
fs.writeFileSync(path.join(root, 'dist', 'build-report.json'), JSON.stringify(report, null, 2));
console.log(JSON.stringify(report, null, 2));
