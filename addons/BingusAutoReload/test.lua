HD2_AUTO_RELOAD_TEST = true
local api = dofile("dist/auto_reload.generated.lua")
local count = 0
local function equal(actual, expected, name)
    assert(actual == expected, (name or "value") .. ": expected " .. tostring(expected) .. ", got " .. tostring(actual))
    count = count + 1
end
local function sample(weapon, ammo, fire)
    return { active = true, weapon = weapon, ammo = ammo, reserve = 5,
        reloading = false, fire = fire or false }
end
local p = api.Policy.new()
equal(p:step(sample("A", 3), 0), nil, "baseline")
equal(p:step(sample("A", 1, true), 0.1), nil, "one round remains")
equal(p:step(sample("A", 0, true), 0.2), "ammo-exhausted", "last round just fired")
p:sent(0.2)
equal(p:step(sample("A", 0, true), 0.6), nil, "held fire does not repeat")
equal(p:step(sample("A", 0, false), 0.7), nil)
equal(p:step(sample("A", 0, true), 0.8), "fire-attempt")
p:sent(0.8)
equal(p:step(sample("B", 0), 1.2), "weapon-swapped")
p:sent(1.2)
equal(p:step(sample("C", 3), 1.6), nil, "loaded swap")
equal(p:step(sample("C", 0, true), 1.7), "ammo-exhausted", "coalesce fire and exhaustion")
p:sent(1.7)
equal(p:step(sample("C", 0), 2.1), nil)
for _, ammo in ipairs({ 1, 2, 0/0, math.huge, -1 }) do
    p = api.Policy.new()
    equal(p:step(sample("A", ammo, true), 0), nil, "not known zero")
end
p = api.Policy.new()
equal(p:step(sample("A", 0), 0), nil, "initial empty is not a swap")
equal(p:step(sample("A", nil, true), 0.1), nil, "unknown is not zero")
equal(p:step(sample("A", 0, false), 0.2), "fire-attempt", "short read gap")
p:sent(0.2)
equal(p:step(sample("B", nil), 0.3), nil)
equal(p:step(sample("B", 0), 0.7), "weapon-swapped", "identity recovered during swap")
p = api.Policy.new()
p:step(sample("A", 1), 0)
local reloading = sample("A", 0)
reloading.reloading = true
equal(p:step(reloading, 0.1), nil, "already reloading")
equal(p:step(sample("A", 0), 0.5), nil, "no duplicate after reload state")
for _, reserve in ipairs({ 0, -1, math.huge, 0/0 }) do
    p = api.Policy.new()
    local s = sample("A", 0, true); s.reserve = reserve
    equal(p:step(s, 0), nil, "no usable reserve")
end
p = api.Policy.new()
local s = sample("A", 0, true); s.reserve = nil; s.reloading = nil
equal(p:step(s, 0), nil, "unknown reload and reserve")
equal(p:step(sample("A", 0, true), 0.1), "fire-attempt", "late eligibility")
p = api.Policy.new()
p:step(s, 0)
equal(p:step(sample("A", 0, true), 0.4), nil, "expired trigger")
p = api.Policy.new()
p:step(sample("A", 2), 0)
equal(p:step({ active = false }, 0.1), nil, "menu/death resets")
equal(p:step(sample("B", 0), 0.2), nil, "no stale swap after menu")
p:reset()
s = sample("B", 0, true); s.manual_reload = true
equal(p:step(s, 0.5), nil, "manual reload not duplicated")
p = api.Policy.new()
p:step(sample("A", 2), 0)
p:step(sample("B", nil), 1)
equal(p:step(sample("B", 0), 1.1), nil, "long identity gap is not guessed")
p = api.Policy.new()
p:step(sample("A", nil, true), 0)
p:reset()
equal(p:step(sample("A", 0), 0.1), nil, "reset removes pending fire")

-- Adapter contracts: a mocked provider never touches the game or sends input.
local cells, resolved, control, rotation, declaration
local equipment = { A = { resource = "content/fac_helldivers/equipment/primary_weapons/test/test" } }
local identity = {
    resolve = function() return resolved end,
    in_control = function() return control end,
    rotation_free = function() return rotation end,
}
local provider = { provide = function() return { cells = cells } end,
    declared_of = function() return declaration end }
local parts = { GeneratedCommon = { identity = { equipment = equipment } },
    IdentityCore = { new = function() return identity end },
    Provider = { new = function() return provider end } }
local reader = api.Reader.new(parts, { snapshot = {} })
resolved = { status = "resolved", avatar = { goid = 100 }, hand_weapon = { goid = 5, type = "A" } }
control, rotation, declaration = true, true, ""
cells = { ammo = 0, reserve = 2, reloading = false }
equal(reader:sample().ammo, 0, "known empty")
equal(reader:sample().weapon, "5:A", "object identity")
equal(reader:sample().reloading, false)
control = false; equal(reader:sample().active, false, "non-player control")
control, rotation = true, false; equal(reader:sample().active, false, "rotation gate")
rotation = true; resolved.hand_weapon = nil
equal(reader:sample().weapon, nil, "no selected weapon")
resolved.hand_weapon = { goid = 5, type = "A" }
cells.heat_shown = 0.9; equal(reader:sample().active, false, "heat is not empty")
cells.heat_shown = nil; resolved.underbarrel = { goid = 5 }
equal(reader:sample().active, false, "base ammo not used for underbarrel")
resolved.underbarrel.goid = 6
equal(reader:sample().active, true, "unused underbarrel does not block main gun")
resolved.underbarrel = nil
equipment.A.resource = "content/fac_helldivers/vehicles/test/test"
equal(reader:sample().active, false, "vehicle skipped")
equipment.A.resource = "content/fac_helldivers/equipment/primary_weapons/test/test"
declaration = "0x4a893e74"
equal(reader:sample().ammo, nil, "unread chamber is not empty")
cells.raw_chamber, cells.ammo = false, 1
equal(reader:sample().ammo, 1, "one chambered round")
declaration = "0xe525fa9c"
cells.raw_slot0, cells.raw_slot1, cells.raw_selected, cells.ammo = 0, 3, 0, 0
equipment.A.ammo_icon = { ["0"] = "buckshot", ["1"] = "buckshot" }
equal(reader:sample().ammo, 3, "other same-ammo feed remains")
equipment.A.ammo_icon["1"] = "stun"
equal(reader:sample().ammo, 0, "different feed does not mask selected empty")
cells.raw_selected = nil; equal(reader:sample().ammo, nil, "unknown selected feed")
cells.raw_selected, cells.raw_slot1 = 0, nil
equal(reader:sample().ammo, nil, "unread second feed")
declaration = ""; equipment.A.spare_pack = { P = true }
equal(reader:sample().reserve, nil, "missing matching backpack")
resolved.backpack = { type = "wrong" }; cells.pack_spare = 4
equal(reader:sample().reserve, nil, "wrong backpack")
resolved.backpack.type = "P"; equal(reader:sample().reserve, 4)
equal(api.boolean(0), false); equal(api.boolean(1), true); equal(api.boolean(nil), nil)

local calls = {}
local env = { update = function(a, b) calls[#calls+1] = "original"; return a, nil, b end,
    shutdown = function(a) return a end }
equal(api.install_hooks(env, function() calls[#calls+1] = "tick" end,
    function() calls[#calls+1] = "stop" end), true)
local a, b, c = env.update(1, 3)
equal(a, 1); equal(b, nil); equal(c, 3)
equal(calls[1], "original"); equal(calls[2], "tick")
equal(env.shutdown(9), 9); equal(calls[3], "stop")
equal(api.install_hooks({}, function() end, function() end), false)

-- Load the actual embedded reader and exercise its real batched ammo provider.
local field_ids = { "d7a5d63e", "4a893e74", "ec64918b", "cd889dbc" }
local raw = { 0, false, 4, false }
stingray = { Network = { object_info = function()
    local fields = {}
    for i, id in ipairs(field_ids) do fields[i] = { id = id } end
    return { fields = fields }
end }, GameSession = {
    game_object_exists = function() return true end,
    game_object_field_batched = function() return raw end,
} }
local real_parts = dofile("dist/reader_core.lua")
local real_fragment = dofile("dist/numbers.lua")
local generated = real_parts.GeneratedCommon
for _, key in ipairs({ "snapshot", "signals", "role_tables", "authored_base", "authored_delta", "network_fields", "relations" }) do
    generated[key] = real_fragment[key] or {}
end
generated.snapshot.reloading = { from = "0xcd889dbc", relation = "hand-weapon" }
local real_provider = real_parts.Provider.new(generated)
local real_type
for key, row in pairs(generated.identity.equipment) do
    if row.resource:find("/primary_weapons/assault_rifle/", 1, true) then real_type = key; break end
end
assert(real_type, "known rifle type missing")
local id = { hand_weapon = { goid = 5, type = real_type } }
equal(real_provider:provide(id, 1).cells.ammo, 1, "actual provider adds chambered round")
raw[2] = true
equal(real_provider:provide(id, 1).cells.ammo, 0, "actual provider empty chamber")
equal(real_provider:provide(id, 1).cells.reserve, 4, "actual reserve reader")
equal(real_provider:provide(id, 1).cells.reloading, false)
raw[1] = 3
equal(real_provider:provide(id, 1).cells.ammo, 3)
raw[4] = true
equal(real_provider:provide(id, 1).cells.reloading, true)
raw[1] = nil
equal(real_provider:provide(id, 1).cells.ammo, nil, "failed read remains unknown")
local ffi = require("ffi")
ffi.cdef[[
typedef struct { unsigned short vk, scan; unsigned int flags, time; uintptr_t extra; } ARTEST_KEY;
typedef struct { int x, y; unsigned int data, flags, time; uintptr_t extra; } ARTEST_MOUSE;
typedef union { ARTEST_KEY key; ARTEST_MOUSE mouse; } ARTEST_UNION;
typedef struct { unsigned int type; ARTEST_UNION value; } ARTEST_INPUT;
]]
equal(ffi.sizeof("ARTEST_INPUT"), 40, "native INPUT ABI")

-- Execute the full runtime with a fake Win32 API. SendInput only records events.
local original_require, original_getenv = require, os.getenv
local keys, inputs, logs, now, focused = {}, {}, {}, 0, true
local user32 = {
    GetAsyncKeyState = function(vk) return keys[vk] and -32768 or 0 end,
    GetForegroundWindow = function() return 1 end,
    GetWindowThreadProcessId = function(_, pid) pid[0] = focused and 42 or 7 end,
    MapVirtualKeyW = function() return 0x13 end,
    SendInput = function(_, input, size)
        equal(size, 40, "runtime ABI")
        inputs[#inputs+1] = { flags = input[0].value.key.flags, scan = input[0].value.key.scan, time = now }
        return 1
    end,
}
local fake_ffi = {
    cdef = function() end,
    load = function(name) return name == "user32" and user32 or { GetCurrentProcessId = function() return 42 end } end,
    sizeof = function() return 40 end,
    abi = function() return true end,
    new = function(name)
        if name == "unsigned int[1]" then return { [0] = 0 } end
        return { [0] = { type = 0, value = { key = {} } } }
    end,
}
require = function(name) if name == "ffi" then return fake_ffi end; return original_require(name) end
os.getenv = function() return nil end
CowboyBingusModLoader = { open_log = function()
    return { write = function(_, text) logs[#logs+1] = text end, flush = function() end }
end }
equipment.A.spare_pack, equipment.A.ammo_icon = nil, nil
resolved = { status = "resolved", avatar = { goid = 100 }, hand_weapon = { goid = 5, type = "A" } }
control, rotation, declaration = true, true, ""
cells = { ammo = 1, reserve = 3, reloading = false }
identity.invalidate = function() end
stingray = {
    Network = { game_session = function() return 1 end, peer_id = function() return 2 end },
    GameSession = { in_session = function() return true end },
    Application = { time_since_launch = function() return now end,
        main_world = function() return 3 end, worlds = function() return {3} end },
}
TEST_READER_PARTS = parts
HD2_AUTO_RELOAD_TEST = nil
local file = assert(io.open("addon.lua", "r")); local source = file:read("*a"); file:close()
file = assert(io.open("policy.lua", "r")); local policy_source = file:read("*a"); file:close()
source = source:gsub("\r\n", "\n")
source = source:gsub("%-%- @POLICY@", function() return policy_source end)
    :gsub("%-%- @READER_CORE@", "return TEST_READER_PARTS")
    :gsub("%-%- @NUMBERS@", "return {snapshot={}}")
update = function() return 123, nil, 321 end
local chunk = assert(loadstring(source, "@addon-runtime-test"))
chunk()
equal(HD2HelperAutoReload ~= nil, true, "runtime initialized")
local function frame(time) now = time; return update() end
a, b, c = frame(0)
equal(a, 123); equal(b, nil); equal(c, 321)
cells.ammo = 0; keys[1] = true
frame(0.021)
equal(#inputs, 1, "runtime exhaustion sends once")
equal(inputs[1].flags, 8, "scan-code down")
frame(0.062)
equal(#inputs, 2, "scheduled up")
equal(inputs[2].flags, 10, "scan-code up")
frame(0.5); equal(#inputs, 2, "held fire not repeated")
keys[1] = false; frame(0.6)
resolved.hand_weapon.goid = 6
frame(0.7); equal(#inputs, 3, "runtime actual swap trigger")
frame(0.75)
keys[1] = true; frame(1.1)
equal(#inputs, 5, "runtime fresh fire attempt")
frame(1.15)
keys[13] = true; frame(1.2)
keys[13], keys[1] = false, false; frame(1.25)
keys[1] = true; frame(1.7)
equal(#inputs, 6, "chat blocks empty fire")
keys[27] = true; frame(1.75)
keys[27], keys[1] = false, false; frame(1.8)
focused = false; keys[1] = true; frame(2)
equal(#inputs, 6, "another application blocks input")
focused, keys[1], keys[119] = true, false, true; frame(2.1)
keys[119], keys[1] = false, true; frame(2.2)
equal(#inputs, 6, "F8 pause blocks")
keys[119], keys[1] = true, false; frame(2.3)
keys[119], keys[1] = false, false; frame(2.35)
cells.ammo = 3; frame(2.4)
cells.ammo = 0; frame(2.5)
equal(#inputs, 7, "resume exhaustion")
shutdown()
equal(#inputs, 8, "shutdown releases outstanding key")
equal(inputs[8].flags, 10)
require, os.getenv = original_require, original_getenv
print("PASS " .. count .. " assertions; actual LuaJIT, no game inputs sent")
