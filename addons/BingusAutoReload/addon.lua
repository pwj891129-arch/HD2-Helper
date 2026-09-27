-- HD2-Addon: mods/hd2_helper/auto_reload
local VERSION = "0.3.0-test"
local Policy = (function()
-- @POLICY@
end)()
local Reader = {}
Reader.__index = Reader

local function boolean(value)
    if value == true or value == 1 then return true end
    if value == false or value == 0 then return false end
    return nil
end

local function valid_count(value)
    return type(value) == "number" and value == value and value >= 0 and
        value < math.huge and value == math.floor(value)
end

function Reader.new(parts, fragment)
    local generated = parts.GeneratedCommon
    for _, name in ipairs({ "snapshot", "signals", "role_tables", "authored_base",
        "authored_delta", "network_fields", "relations" }) do
        generated[name] = fragment[name] or {}
    end
    local fields = generated.snapshot
    for name, hash in pairs({ raw_slot0 = "0x04ec5b95", raw_slot1 = "0xe525fa9c",
        raw_selected = "0xa74ef0d5", raw_chamber = "0x4a893e74",
        reloading = "0xcd889dbc" }) do
        fields[name] = { from = hash, relation = "hand-weapon" }
    end
    return setmetatable({ generated = generated,
        identity = parts.IdentityCore.new(generated.identity),
        provider = parts.Provider.new(generated) }, Reader)
end

function Reader:sample(session, world, peer)
    local resolved = self.identity:resolve(session, world, peer)
    if not resolved or not resolved.avatar or resolved.status ~= "resolved" then
        return { active = false }, resolved and resolved.reason or "no-avatar"
    end
    if boolean(self.identity:in_control(session, resolved.avatar)) ~= true or
        boolean(self.identity:rotation_free(session, resolved.avatar)) ~= true then
        return { active = false }, "no-player-control"
    end
    local hand = resolved.hand_weapon
    if not hand then return { active = true }, "no-held-weapon" end
    local spec = self.generated.identity.equipment[hand.type]
    local resource = spec and spec.resource or ""
    if not string.find(resource, "/equipment/primary_weapons/", 1, true) and
        not string.find(resource, "/equipment/secondary_weapons/", 1, true) and
        not string.find(resource, "/equipment/support_weapons/", 1, true) then
        return { active = false }, "unsupported-held-item"
    end
    if resolved.underbarrel and resolved.underbarrel.goid == hand.goid then
        return { active = false }, "underbarrel-not-supported"
    end
    local cells = self.provider:provide(resolved, session).cells
    if cells.heat_shown ~= nil then
        return { active = false }, "heat-weapon-not-supported"
    end
    local ammo = cells.ammo
    local declared = self.provider:declared_of(hand.type) or ""
    if string.find(declared, "0x4a893e74", 1, true) and
        boolean(cells.raw_chamber) == nil then ammo = nil end
    if string.find(declared, "0xe525fa9c", 1, true) then
        if not valid_count(cells.raw_slot0) or not valid_count(cells.raw_slot1) or
            (cells.raw_selected ~= 0 and cells.raw_selected ~= 1) then
            ammo = nil
        else
            local icons = spec.ammo_icon
            -- Identical feeds (e.g. Punisher) still have usable ammo in the other tube.
            if icons and icons["0"] and icons["0"] == icons["1"] and ammo ~= nil then
                ammo = ammo + (cells.raw_selected == 0 and cells.raw_slot1 or cells.raw_slot0)
            end
        end
    end
    if not valid_count(ammo) then ammo = nil end
    local reserve = cells.reserve
    if spec.spare_pack then
        local pack = resolved.backpack
        reserve = pack and spec.spare_pack[pack.type] and cells.pack_spare or nil
    end
    return { active = true,
        weapon = tostring(hand.goid) .. ":" .. tostring(hand.type),
        ammo = ammo, reserve = reserve, reloading = boolean(cells.reloading),
        avatar = resolved.avatar.goid }, ammo == nil and "ammo-unavailable" or "ready"
end

local function install_hooks(env, tick, stop)
    local original_update = env.update
    if type(original_update) ~= "function" then return false end
    local function pack(...) return { n = select("#", ...), ... } end
    env.update = function(...)
        local result = pack(original_update(...))
        tick()
        return unpack(result, 1, result.n)
    end
    local original_shutdown = env.shutdown
    env.shutdown = function(...)
        stop()
        if type(original_shutdown) == "function" then return original_shutdown(...) end
    end
    return true
end

if rawget(_G, "HD2_AUTO_RELOAD_TEST") then
    return { Policy = Policy, Reader = Reader, boolean = boolean,
        install_hooks = install_hooks }
end
if rawget(_G, "HD2HelperAutoReload") then return end

local parts = (function()
-- @READER_CORE@
end)()
local fragment = (function()
-- @NUMBERS@
end)()
local ok, reader = pcall(Reader.new, parts, fragment)
local loader = rawget(_G, "CowboyBingusModLoader")
local log_ok, log_file = pcall(function()
    return loader and loader.open_log and loader.open_log("hd2_helper_auto_reload.log")
end)
if not log_ok then log_file = nil end
local function log(line)
    if log_file then pcall(function() log_file:write(tostring(line), "\n"); log_file:flush() end) end
end
if not ok then log("DISABLED reader initialization: " .. tostring(reader)); return end

local config = { fire_vk = 1, reload_vk = 82, pause_vk = 119, enabled = true }
local appdata = os.getenv("APPDATA")
local config_path = appdata and (appdata .. "\\HD2AutoReload.ini")
if config_path then
    local file = io.open(config_path, "r")
    if file then
        for line in file:lines() do
            local key, value = line:match("^%s*([%w_]+)%s*=%s*([^;]+)")
            if key == "enabled" then config.enabled = value:match("^%s*true%s*$") ~= nil
            elseif key and key:match("_vk$") and config[key] ~= nil then
                local number = tonumber(value)
                if number and number >= 1 and number <= 254 and number == math.floor(number) then
                    config[key] = number
                end
            end
        end
        file:close()
    else
        file = io.open(config_path, "w")
        if file then
            file:write("; Windows virtual-key codes. F8 pauses/resumes.\n",
                "enabled=true\nfire_vk=1\nreload_vk=82\npause_vk=119\n")
            file:close()
        end
    end
end
if config.reload_vk <= 6 or config.reload_vk == config.fire_vk or
    config.reload_vk == config.pause_vk then
    log("DISABLED conflicting/unsupported reload key"); return
end
local ffi_ok, ffi = pcall(require, "ffi")
if not ffi_ok then log("DISABLED LuaJIT FFI unavailable"); return end
local native_ok, native = pcall(function()
    ffi.cdef[[
    typedef struct { unsigned short vk, scan; unsigned int flags, time; uintptr_t extra; } HD2AR_KEY;
    typedef struct { int x, y; unsigned int data, flags, time; uintptr_t extra; } HD2AR_MOUSE;
    typedef union { HD2AR_KEY key; HD2AR_MOUSE mouse; } HD2AR_UNION;
    typedef struct { unsigned int type; HD2AR_UNION value; } HD2AR_INPUT;
    void* GetForegroundWindow(void);
    unsigned int GetWindowThreadProcessId(void*, unsigned int*);
    unsigned int GetCurrentProcessId(void);
    short GetAsyncKeyState(int);
    unsigned int SendInput(unsigned int, const HD2AR_INPUT*, int);
    unsigned int MapVirtualKeyW(unsigned int, unsigned int);
    ]]
    local user32, kernel32 = ffi.load("user32"), ffi.load("kernel32")
    local process = kernel32.GetCurrentProcessId()
    local size = ffi.sizeof("HD2AR_INPUT")
    assert(size == (ffi.abi("64bit") and 40 or 28), "INPUT layout mismatch")
    local pid = ffi.new("unsigned int[1]")
    local input = ffi.new("HD2AR_INPUT[1]")
    input[0].type = 1
    local scan = user32.MapVirtualKeyW(config.reload_vk, 4)
    assert(scan ~= 0, "reload key has no scan code")
    input[0].value.key.scan = scan % 256
    local flags = scan >= 256 and 9 or 8
    return { user32 = user32, process = process, pid = pid, input = input,
        size = size, flags = flags }
end)
if not native_ok then log("DISABLED input initialization: " .. tostring(native)); return end

local sr = rawget(_G, "stingray") or {}
local Net, GS, App = sr.Network or {}, sr.GameSession or {}, sr.Application or {}
if type(App.time_since_launch) ~= "function" then log("DISABLED monotonic clock unavailable"); return end
local policy, state = Policy.new(), { paused = not config.enabled, keys = {} }
rawset(_G, "HD2HelperAutoReload", state)

local function down(vk) return native.user32.GetAsyncKeyState(vk) < 0 end
local function release()
    if state.release_at then
        native.input[0].value.key.flags = native.flags + 2
        local sent = native.user32.SendInput(1, native.input, native.size)
        if sent == 1 then state.release_at = nil end
    end
end
local function foreground()
    native.user32.GetWindowThreadProcessId(native.user32.GetForegroundWindow(), native.pid)
    return native.pid[0] == native.process
end
local function scope()
    local session = Net.game_session and Net.game_session()
    if not session or not GS.in_session or GS.in_session(session) ~= true then return nil end
    local world, peer = App.main_world and App.main_world(), Net.peer_id and Net.peer_id()
    if not world or peer == nil or not App.worlds then return nil end
    local worlds = App.worlds()
    if type(worlds) ~= "table" then return nil end
    for _, value in pairs(worlds) do if value == world then return session, world, peer end end
end
local function status(reason, sample)
    local label = tostring(reason) .. " weapon=" .. tostring(sample and sample.weapon)
    local text = label ..
        " ammo=" .. tostring(sample and sample.ammo) .. " reserve=" .. tostring(sample and sample.reserve)
    if label ~= state.status then log(text); state.status = label end
end
local function tick()
    local now = App.time_since_launch()
    if type(now) ~= "number" then return end
    if state.release_at and (now >= state.release_at or not foreground()) then release() end
    local focused = foreground()
    local keys = { enter = down(13), escape = down(27), tab = down(9), pause = down(config.pause_vk) }
    if focused then
        if keys.pause and not state.keys.pause then
            state.paused = not state.paused
            log(state.paused and "PAUSED" or "RESUMED")
        end
        -- Keep chat blocked until its close/send key; control gates also guard mouse-opened menus.
        if keys.enter and not state.keys.enter then state.chat = not state.chat end
        if keys.escape and not state.keys.escape then state.chat = false end
    end
    state.keys = keys
    local fire = focused and down(config.fire_vk)
    if fire and not state.fire then state.fire_pending = true end
    state.fire = fire
    if not focused or state.paused or state.failed or state.chat or keys.enter or keys.escape or keys.tab then
        policy:reset(); state.fire_pending = nil; release(); return
    end
    if state.next_read and now < state.next_read then return end
    state.next_read = now + 0.02
    local session, world, peer = scope()
    if not session then
        policy:reset(); reader.identity:invalidate(); state.avatar = nil
        state.fire_pending = nil; status("no-session"); return
    end
    local context = tostring(session) .. ":" .. tostring(world) .. ":" .. tostring(peer)
    if state.context ~= context then
        policy:reset(); reader.identity:invalidate(); state.context = context; state.avatar = nil
    end
    local sample, reason = reader:sample(session, world, peer)
    if sample.avatar and state.avatar and sample.avatar ~= state.avatar then policy:reset() end
    state.avatar = sample.avatar
    sample.fire = fire or state.fire_pending == true
    sample.manual_reload = down(config.reload_vk) or state.release_at ~= nil
    state.fire_pending = nil
    status(reason, sample)
    local trigger = policy:step(sample, now)
    if trigger and not state.release_at and foreground() then
        native.input[0].value.key.flags = native.flags
        if native.user32.SendInput(1, native.input, native.size) == 1 then
            state.release_at = now + 0.04
            policy:sent(now)
            log("RELOAD " .. trigger .. " " .. tostring(sample.weapon))
        else
            log("INPUT_FAILED " .. trigger)
            policy:sent(now)
        end
    end
end
local function guarded_tick()
    local success, failure = pcall(tick)
    if not success then
        pcall(release); policy:reset(); state.failed = true
        log("DISABLED runtime error: " .. tostring(failure))
    end
end
if not install_hooks(_G, guarded_tick, function() pcall(release) end) then
    log("DISABLED update callback unavailable"); rawset(_G, "HD2HelperAutoReload", nil); return
end
log("START " .. VERSION .. " fire_vk=" .. config.fire_vk .. " reload_vk=" .. config.reload_vk)
