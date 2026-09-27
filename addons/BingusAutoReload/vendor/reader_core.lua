-- Derived from HD2 HUD+ 0.1.2 by DDRK1NG; see THIRD_PARTY.txt.
local __module_registry = {}
local __module_errors = {}
local function __module_log(line)
__module_errors[#__module_errors + 1] = tostring(line)
end
local function __module_environment(name, ambient_global)
local allowed = {}
allowed["assert"] = rawget(_G, "assert")
allowed["collectgarbage"] = rawget(_G, "collectgarbage")
allowed["error"] = rawget(_G, "error")
allowed["getmetatable"] = rawget(_G, "getmetatable")
allowed["io"] = rawget(_G, "io")
allowed["ipairs"] = rawget(_G, "ipairs")
allowed["load"] = rawget(_G, "load")
allowed["loadstring"] = rawget(_G, "loadstring")
allowed["math"] = rawget(_G, "math")
allowed["next"] = rawget(_G, "next")
allowed["os"] = rawget(_G, "os")
allowed["pairs"] = rawget(_G, "pairs")
allowed["pcall"] = rawget(_G, "pcall")
allowed["rawget"] = rawget(_G, "rawget")
allowed["rawset"] = rawget(_G, "rawset")
allowed["select"] = rawget(_G, "select")
allowed["setmetatable"] = rawget(_G, "setmetatable")
allowed["string"] = rawget(_G, "string")
allowed["table"] = rawget(_G, "table")
allowed["tonumber"] = rawget(_G, "tonumber")
allowed["tostring"] = rawget(_G, "tostring")
allowed["type"] = rawget(_G, "type")
allowed["unpack"] = rawget(_G, "unpack")
if ambient_global then allowed._G = _G end
return setmetatable(allowed, {__index=function(_, key) error(name .. ": undeclared global " .. tostring(key), 0) end,__newindex=function(_, key) error(name .. ": global write " .. tostring(key), 0) end})
end
local __module_run = (function()
return function(name, factory, imports, log)
local ok, exports = pcall(factory, imports)
if not ok then log("MODULE " .. name .. " ERROR " .. tostring(exports)) return nil end
if type(exports) ~= "table" then log("MODULE " .. name .. " ERROR exports") return nil end
return exports
end
end)()
do
local __imports = {}
local __factory = (function()
return function(imports)
if type(imports) ~= "table" then error("20-identity-core: imports must be a table", 0) end
local IdentityCore = (function()
  local Core = { VERSION = "identity-v3" }
  local sr = rawget(_G, "stingray")
  local GS = sr and rawget(sr, "GameSession") or nil
  local World = sr and rawget(sr, "World") or nil
  local Unit = sr and rawget(sr, "Unit") or nil
  local Net = sr and rawget(sr, "Network") or nil
  local Vector3 = sr and rawget(sr, "Vector3") or nil
  local IdString64 = sr and rawget(sr, "IdString64") or nil
  local UNRESOLVED = "unknown"
  local ABSENT = "absent"
  Core.RELEASES = { [ABSENT] = true }
  local NO_AVATAR_NOW = "no-avatar-right-now"
  local function callable(namespace, name)
    return type(namespace) == "table" and
           type(rawget(namespace, name)) == "function"
  end
  local function call(counters, fn, ...)
    local result = { pcall(fn, ...) }
    if result[1] ~= true then
      counters.errors = counters.errors + 1
      return nil
    end
    counters.calls = counters.calls + 1
    return result[2]
  end
  local function call_many(counters, fn, ...)
    local function collect(ok, ...)
      if ok ~= true then
        counters.errors = counters.errors + 1
        return nil
      end
      counters.calls = counters.calls + 1
      return { n = select("#", ...), ... }
    end
    return collect(pcall(fn, ...))
  end
  local function surface_missing()
    if sr == nil then return "stingray" end
    local names = {
      { GS, "GameSession", { "objects_owned_by", "game_object_exists",
                             "game_object_is_type", "game_object_field" } },
      { World, "World", { "units_by_resource" } },
      { Unit, "Unit", { "alive", "node", "world_position",
                        "animation_has_variable", "animation_find_variable",
                        "animation_get_variable",
                        "has_animation_state_machine" } },
      { Net, "Network", { "object_info" } },
      { Vector3, "Vector3", { "distance" } },
    }
    for index = 1, #names do
      local namespace, label, members = names[index][1], names[index][2],
                                        names[index][3]
      if type(namespace) ~= "table" then return label end
      for member_index = 1, #members do
        if not callable(namespace, members[member_index]) then
          return label .. "." .. members[member_index]
        end
      end
    end
    return nil
  end
  local NO_OBJECT = 32767
  local Identity = {}
  Identity.__index = Identity
  function Core.new(authored)
    local self = setmetatable({}, Identity)
    self.authored = authored or {}
    self.avatar = self.authored.avatar or {}
    self.player = self.authored.player or {}
    self.carry = self.authored.carry_field or {}
    self.account = self.authored.account_field or {}
    self.equipment = self.authored.equipment or {}
    self.equipment_defaults = self.authored.equipment_defaults or {}
    self.backpacks = self.authored.backpacks or {}
    self.underbarrels = self.authored.underbarrels or {}
    self.generation = nil
    self.object_type = {}
    self.type_missed = {}
    self.type_fields = {}
    self.type_order = {}
    self.last_hand = nil
    self.out = nil
    self.base = nil
    self.buckets = nil
    self.counters = {
      calls = 0, errors = 0, resolves = 0, unknowns = 0, absents = 0,
      invalidations = 0,
      exists_misses = 0, type_calls = 0,
      type_field_reads = 0, mask_reads = 0,
      mask_undeclared = 0, mask_unreadable = 0,
      declared_reads = 0,
    }
    return self
  end
  function Identity:reset(generation)
    self.generation = generation
    self.object_type = {}
    self.type_missed = {}
    self.last_hand = nil
    self.carried_goid = nil
    self.node_index = {}
    self.grip_index = {}
    self.player_goid = nil
    self.account_id = nil
    self.account_asked = nil
    self.counters.invalidations = self.counters.invalidations + 1
  end
  function Identity:account_of(session, hand)
    if self.account_asked then return self.account_id end
    if hand == nil or hand.goid == nil then return nil end
    local field = self.account.call
    if field == nil then return nil end
    local declared = self:declared_fields(hand.type, self:call_of(hand.type))
    if declared[self.account.hash or ""] ~= true then
      self.account_asked = true
      self.counters.account_undeclared =
        (self.counters.account_undeclared or 0) + 1
      return nil
    end
    self.account_asked = true
    local value = call(self.counters, GS.game_object_field,
                       session, hand.goid, field)
    if type(value) == "string" and #value >= 8 then
      self.account_id = value
      self.counters.account_read = (self.counters.account_read or 0) + 1
    else
      self.counters.account_unreadable =
        (self.counters.account_unreadable or 0) + 1
    end
    return self.account_id
  end
  function Identity:invalidate()
    self.generation = nil
  end
  function Identity:sync(session, world)
    local token = tostring(session) .. "/" .. tostring(world)
    if self.generation ~= token then self:reset(token) end
  end
  function Identity:live(session, goid)
    local alive = call(self.counters, GS.game_object_exists, session, goid)
    if alive ~= true then
      self.counters.exists_misses = self.counters.exists_misses + 1
      return false
    end
    return true
  end
  function Identity:node_position(unit, index)
    if index == nil then return nil end
    local position = call(self.counters, Unit.world_position, unit, index)
    if position == nil then return nil end
    return position
  end
  function Identity:node_of(unit, node_name)
    local per_unit = self.node_index[unit] or {}
    self.node_index[unit] = per_unit
    local known = per_unit[node_name]
    if known ~= nil then
      self.counters.node_cached = (self.counters.node_cached or 0) + 1
      return known
    end
    local index = call(self.counters, Unit.node, unit, node_name)
    per_unit[node_name] = index
    return index
  end
  function Identity:body_positions(world, node_name)
    local bodies = self:units_of(world, self.avatar.third_person)
    local out = {}
    for index = 1, #bodies do
      local node = self:node_of(bodies[index], node_name)
      local position = self:node_position(bodies[index], node)
      if position ~= nil then out[#out + 1] = position end
    end
    return out
  end
  function Identity:narrow_by_wield(world, candidates)
    self.hand_unit = nil
    if self.avatar.third_person == nil then
      return candidates, "no-third-person-resource"
    end
    local nodes = {}
    local held = {}
    local saw_unit = false
    for index = 1, #candidates do
      local row = self.equipment[candidates[index].type]
      local node = row ~= nil and
        (row.wield_node or self.equipment_defaults.wield_node) or nil
      if row ~= nil and row.resource ~= nil and node ~= nil then
        local anchors = nodes[node]
        if anchors == nil then
          anchors = self:body_positions(world, node)
          nodes[node] = anchors
        end
        if #anchors == 0 then
          self.counters.wield_no_body = (self.counters.wield_no_body or 0) + 1
        end
        if #anchors > 0 then
          local offset = row.wield_offset or
                         self.equipment_defaults.wield_offset or 0
          local units = self:units_of(world, row.resource)
          if #units == 0 then
            self.counters.wield_no_unit =
              (self.counters.wield_no_unit or 0) + 1
          end
          for u = 1, #units do
            saw_unit = true
            local position = self:node_position(units[u], 1)
            if position ~= nil then
              for a = 1, #anchors do
                local span = call(self.counters, Vector3.distance,
                                  anchors[a], position)
                if type(span) == "number"
                   and math.abs(span - offset) == 0 then
                  held[candidates[index].type] = true
                  self.hand_unit = units[u]
                end
              end
            end
          end
        end
      end
    end
    local kept = {}
    for index = 1, #candidates do
      if held[candidates[index].type] then kept[#kept + 1] = candidates[index] end
    end
    if #kept > 0 then return kept, "narrowed" end
    return kept, saw_unit and "nothing-at-the-wield-node" or "no-candidate-unit"
  end
  function Identity:first_person_hand(world, candidates)
    local resource = self.avatar.first_person
    if resource == nil then return {} end
    local rigs = self:units_of(world, resource)
    if #rigs ~= 1 then return {} end
    local name = self.equipment_defaults.wield_node
    if name == nil then return {} end
    local seat = self:node_of(rigs[1], name)
    local anchor = self:node_position(rigs[1], seat)
    if anchor == nil then return {} end
    local held = {}
    for index = 1, #candidates do
      local row = self.equipment[candidates[index].type]
      if row ~= nil and row.resource ~= nil then
        local offset = row.wield_offset or
                       self.equipment_defaults.wield_offset or 0
        local units = self:units_of(world, row.resource)
        for u = 1, #units do
          local at = self:node_position(units[u], 1)
          if at ~= nil then
            local span = call(self.counters, Vector3.distance, anchor, at)
            if type(span) == "number" and math.abs(span - offset) == 0 then
              held[candidates[index].type] = true
              self.hand_unit = units[u]
            end
          end
        end
      end
    end
    local kept = {}
    for index = 1, #candidates do
      if held[candidates[index].type] then kept[#kept + 1] = candidates[index] end
    end
    if #kept > 0 then
      self.counters.first_person = (self.counters.first_person or 0) + 1
    end
    return kept
  end
  function Identity:carried_hand(world, candidates)
    local goid = self.carried_goid
    if goid == nil then return {} end
    for index = 1, #candidates do
      if candidates[index].goid == goid then
        self.counters.carried = (self.counters.carried or 0) + 1
        local row = self.equipment[candidates[index].type]
        local units = row ~= nil and self:units_of(world, row.resource) or {}
        if #units == 1 then
          self.hand_unit = units[1]
          self.counters.carried_unit = (self.counters.carried_unit or 0) + 1
        elseif row == nil then
          self.counters.carried_no_row = (self.counters.carried_no_row or 0) + 1
        elseif #units == 0 then
          self.counters.carried_no_unit =
            (self.counters.carried_no_unit or 0) + 1
        else
          self.counters.carried_many_units =
            (self.counters.carried_many_units or 0) + 1
        end
        return { candidates[index] }
      end
    end
    return {}
  end
  function Identity:base_candidates()
    if self.base ~= nil then return self.base end
    local rows = {}
    if self.avatar.type ~= nil then
      rows[#rows + 1] = self.avatar.type
    end
    if self.player.type ~= nil then
      rows[#rows + 1] = self.player.type
    end
    for hash in pairs(self.backpacks) do
      rows[#rows + 1] = hash
    end
    table.sort(rows)
    self.base = rows
    return rows
  end
  function Identity:grip_bucket(grip)
    if grip == nil then return nil end
    if self.buckets == nil then self.buckets = {} end
    local rows = self.buckets[grip]
    if rows ~= nil then return rows end
    rows = {}
    for hash, row in pairs(self.equipment) do
      if row.grip == grip then rows[#rows + 1] = hash end
    end
    table.sort(rows)
    self.buckets[grip] = rows
    return rows
  end
  function Identity:call_of(hash)
    local row = self.equipment[hash] or self.backpacks[hash]
      or self.underbarrels[hash]
    if row ~= nil then return row.call end
    if hash == self.avatar.type then return self.avatar.call end
    if hash == self.player.type then return self.player.call end
    return nil
  end
  function Identity:all_rows()
    if self.all ~= nil then return self.all end
    local rows = {}
    for hash in pairs(self.equipment) do
      rows[#rows + 1] = hash
    end
    for hash in pairs(self.backpacks) do
      rows[#rows + 1] = hash
    end
    for hash in pairs(self.underbarrels) do
      rows[#rows + 1] = hash
    end
    table.sort(rows)
    self.all = rows
    return rows
  end
  function Identity:scan_types(session, goid, missed, scope, rows)
    if rows == nil or missed[scope] then return nil end
    for index = 1, #rows do
      local hash = rows[index]
      local name = self:call_of(hash)
      if type(name) == "string" then
        self.counters.type_calls = self.counters.type_calls + 1
        local is_type = call(self.counters, GS.game_object_is_type,
                             session, goid, name)
        if is_type == true then
          self.object_type[goid] = hash
          return hash
        end
      end
    end
    missed[scope] = true
    return nil
  end
  function Identity:used_this_window(hash, grip)
    if hash == self.avatar.type or hash == self.player.type then return true end
    if self.backpacks[hash] ~= nil then return true end
    local row = self.equipment[hash]
    return row ~= nil and row.grip == grip
  end
  function Identity:type_of(session, goid, grip)
    local known = self.object_type[goid]
    if known ~= nil and not self:used_this_window(known, grip) then
      self.counters.type_skipped = (self.counters.type_skipped or 0) + 1
      return nil
    end
    if known ~= nil then
      local name = self:call_of(known)
      local still = nil
      if type(name) == "string" then
        self.counters.type_calls = self.counters.type_calls + 1
        still = call(self.counters, GS.game_object_is_type, session, goid, name)
      end
      if still == true then return known end
      self.object_type[goid] = nil
      self.type_missed[goid] = nil
      self.counters.type_recycled = (self.counters.type_recycled or 0) + 1
    end
    local missed = self.type_missed[goid]
    if missed == nil then
      missed = {}
      self.type_missed[goid] = missed
    end
    local hash = self:scan_types(session, goid, missed, "base",
                                 self:base_candidates())
    if hash ~= nil then return hash end
    return self:scan_types(session, goid, missed, grip,
                           self:grip_bucket(grip))
  end
  function Identity:owned_objects(session, my_peer, grip)
    local owned = call(self.counters, GS.objects_owned_by, session, my_peer)
    if type(owned) ~= "table" then return nil end
    local rows = {}
    self.counters.scans = (self.counters.scans or 0) + 1
    local seen = 0
    for _, goid in pairs(owned) do
      seen = seen + 1
      local hash = self:type_of(session, goid, grip)
      if hash ~= nil then
        rows[#rows + 1] = { goid = goid, type = hash }
      end
    end
    self.counters.owned_seen = seen
    return rows
  end
  function Identity:resource_key(resource)
    if type(resource) ~= "string" then return nil end
    local hex = string.match(resource, "^0[xX](%x%x%x%x%x%x%x%x%x%x%x%x%x%x%x%x)$")
    if hex == nil then return resource end
    if self.resource_ids == nil then self.resource_ids = {} end
    local known = self.resource_ids[resource]
    if known ~= nil then
      if known == false then return nil end
      return known
    end
    if not callable(IdString64, "from_hex") then
      self.resource_ids[resource] = false
      return nil
    end
    local id = call(self.counters, IdString64.from_hex, hex)
    self.resource_ids[resource] = id or false
    if id == nil then return nil end
    return id
  end
  function Identity:units_of(world, resource)
    local key = self:resource_key(resource)
    if key == nil then return {} end
    local found = call(self.counters, World.units_by_resource, world, key)
    if type(found) ~= "table" then return {} end
    local units = {}
    for _, unit in pairs(found) do
      if call(self.counters, Unit.alive, unit) == true then
        units[#units + 1] = unit
      end
    end
    return units
  end
  function Identity:current_grip(world)
    local resource = self.avatar.first_person
    if resource == nil then return nil, "no-first-person-resource" end
    local units = self:units_of(world, resource)
    if #units ~= 1 then
      return nil, "first-person-units=" .. tostring(#units)
    end
    return self:grip_variable_of(units[1])
  end
  function Identity:grip_variable_of(unit)
    local name = self.avatar.grip_variable or "weapon_selected"
    local index = self.grip_index[unit]
    if index ~= nil then
      self.counters.grip_cached = (self.counters.grip_cached or 0) + 1
      local cached = call(self.counters, Unit.animation_get_variable, unit, index)
      if type(cached) ~= "number" then return nil, "variable-unreadable" end
      return cached, nil
    end
    if call(self.counters, Unit.has_animation_state_machine, unit) ~= true then
      return nil, "no-animation-state-machine"
    end
    if call(self.counters, Unit.animation_has_variable, unit, name) ~= true then
      return nil, "no-variable:" .. name
    end
    index = call(self.counters, Unit.animation_find_variable, unit, name)
    if type(index) ~= "number" then return nil, "variable-index-missing" end
    self.grip_index[unit] = index
    local value = call(self.counters, Unit.animation_get_variable, unit, index)
    if type(value) ~= "number" then return nil, "variable-unreadable" end
    return value, nil
  end
  local ACTION_INFINITE = 1e30
  function Identity:action_layer(world, layer)
    local units = self:units_of(world, self.avatar.first_person)
    if #units ~= 1 then return nil end
    local unit = units[1]
    if call(self.counters, Unit.has_animation_state_machine, unit) ~= true then
      return nil
    end
    local packed = call_many(self.counters, Unit.animation_get_state, unit)
    if packed == nil or layer < 1 or layer > packed.n - 1 then return nil end
    local state = packed[layer + 1]
    if state == nil then return nil end
    local info = call_many(self.counters, Unit.animation_layer_info, unit, layer)
    if info == nil or info.n < 2 then return nil end
    local elapsed, length = info[1], info[2]
    if type(elapsed) ~= "number" or type(length) ~= "number" then return nil end
    if length <= 0 or length >= ACTION_INFINITE then return nil end
    self.counters.action_reads = (self.counters.action_reads or 0) + 1
    return { elapsed = elapsed, length = length, state = state }
  end
  function Identity:reloading_by_rig(hand)
    local row = self.equipment[hand.type]
    local rig = row ~= nil and row.reload or nil
    if rig == nil then
      self.counters.action_no_rig = (self.counters.action_no_rig or 0) + 1
      return false
    end
    local unit = self.hand_unit
    if unit == nil then return false end
    if call(self.counters, Unit.alive, unit) ~= true then
      self.counters.action_unit_gone =
        (self.counters.action_unit_gone or 0) + 1
      return false
    end
    if call(self.counters, Unit.has_animation_state_machine, unit) ~= true then
      return false
    end
    local packed = call_many(self.counters, Unit.animation_get_state, unit)
    if packed == nil then return false end
    self.counters.action_gate_reads =
      (self.counters.action_gate_reads or 0) + 1
    for layer = 0, packed.n - 1 do
      local seats = rig[layer]
      if seats ~= nil then
        local state = packed[layer + 1]
        if state ~= nil then
          for index = 1, #seats do
            if seats[index] == state then
              self.counters.action_gate_open =
                (self.counters.action_gate_open or 0) + 1
              return true
            end
          end
        end
      end
    end
    return false
  end
  function Identity:action_of(world, hand)
    local spec = self.authored.action
    if type(spec) ~= "table" or type(spec.layer) ~= "number" then return nil end
    if hand == nil or hand.goid == nil then
      self.action_span = nil
      return nil
    end
    local span = self.action_span
    if span ~= nil and span.goid ~= hand.goid then span = nil end
    if span == nil then
      self.action_span = nil
      if not self:reloading_by_rig(hand) then return nil end
    else
      self.counters.action_carried = (self.counters.action_carried or 0) + 1
    end
    local reading = self:action_layer(world, spec.layer)
    if reading == nil then
      self.action_span = nil
      return nil
    end
    if span ~= nil then
      if reading.state ~= span.state then
        self.counters.action_cut_state =
          (self.counters.action_cut_state or 0) + 1
        self.action_span = nil
        return nil
      end
      if reading.length ~= span.length then
        self.counters.action_cut_clip =
          (self.counters.action_cut_clip or 0) + 1
        self.action_span = nil
        return nil
      end
      if reading.elapsed < span.elapsed then
        self.counters.action_cut_rewind =
          (self.counters.action_cut_rewind or 0) + 1
        self.action_span = nil
        return nil
      end
      if reading.elapsed == span.elapsed then
        self.counters.action_still = (self.counters.action_still or 0) + 1
      end
    end
    if reading.elapsed >= reading.length then
      self.counters.action_span_done =
        (self.counters.action_span_done or 0) + 1
      self.action_span = nil
    else
      self.action_span = { goid = hand.goid, length = reading.length,
                           elapsed = reading.elapsed, state = reading.state }
    end
    return reading
  end
  function Identity:declared_fields(type_hash, call_string)
    local known = self.type_fields[type_hash]
    if known ~= nil then return known end
    local set = {}
    local order = {}
    if type(call_string) == "string" then
      local info = call(self.counters, Net.object_info, call_string)
      if type(info) == "table" then
        local ok = pcall(function()
          local fields = info.fields
          if type(fields) ~= "table" then return end
          for index = 1, #fields do
            local id = fields[index].id
            if id ~= nil then
              local hex = string.lower(tostring(id))
              if string.sub(hex, 1, 2) == "0x" then hex = string.sub(hex, 3) end
              while string.len(hex) < 8 do hex = "0" .. hex end
              set["0x" .. hex] = true
              order[index] = "0x" .. hex
            end
          end
        end)
        if not ok then set, order = {}, {} end
      end
    end
    self.type_order[type_hash] = order
    self.type_fields[type_hash] = set
    self.counters.type_field_reads = self.counters.type_field_reads + 1
    return set
  end
  function Identity:bound_elsewhere(session, row, call_string)
    local row_field = self.authored.bind_field
    if type(row_field) ~= "table" or row_field.call == nil then return false end
    local declared = self:declared_fields(row.type, call_string)
    if declared[row_field.hash or ""] ~= true then return false end
    local value = call(self.counters, GS.game_object_field,
                      session, row.goid, row_field.call)
    if type(value) ~= "number" then return false end
    self.counters.bind_reads = (self.counters.bind_reads or 0) + 1
    if value == NO_OBJECT then return false end
    self.counters.bound_elsewhere = (self.counters.bound_elsewhere or 0) + 1
    return true, value
  end
  function Identity:on_my_body(session, row, call_string)
    local bound, target = self:bound_elsewhere(session, row, call_string)
    if bound then return false, "bound-to:" .. tostring(target) end
    local field = self.carry.call
    if field == nil then return true, "no-authored-carry-field" end
    local declared = self:declared_fields(row.type, call_string)
    if declared[self.carry.hash or ""] ~= true then
      self.counters.mask_undeclared = self.counters.mask_undeclared + 1
      return true, "type-does-not-declare-it"
    end
    local value = call(self.counters, GS.game_object_field,
                      session, row.goid, field)
    if type(value) ~= "number" then
      self.counters.mask_unreadable = self.counters.mask_unreadable + 1
      return false, "unreadable"
    end
    self.counters.mask_reads = self.counters.mask_reads + 1
    if value % 4 == 0 then return true, "on-body", value end
    return false, "carry=" .. tostring(value), value
  end
  local function fingerprint(value)
    local kind = type(value)
    if kind == "number" or kind == "boolean" or kind == "string" then
      return tostring(value)
    end
    if kind == "userdata" then return "u:" .. tostring(value) end
    if kind ~= "table" then return kind end
    local parts, seen = {}, 0
    for key, one in pairs(value) do
      local inner = type(one)
      if inner == "number" or inner == "boolean" or inner == "string" then
        seen = seen + 1
        if seen <= 8 then
          parts[#parts + 1] = tostring(key) .. "=" .. tostring(one)
        end
      end
    end
    table.sort(parts)
    return "t" .. tostring(seen) .. "{" .. table.concat(parts, ";") .. "}"
  end
  function Identity:declared_values(session, cand)
    if cand == nil or cand.goid == nil then return nil, "no-goid" end
    local order = self.type_order[cand.type]
    if type(order) ~= "table" then return nil, "no-declaration" end
    if self.out == nil then self.out = {} end
    for key in pairs(self.out) do self.out[key] = nil end
    self.counters.declared_reads = self.counters.declared_reads + 1
    local ok, value = pcall(GS.game_object_field_batched, session,
                            cand.goid, self.out)
    if not ok then return nil, "batched-threw" end
    if type(value) ~= "table" then return nil, "batched-gave-no-table" end
    local out = {}
    for seat, one in pairs(value) do
      local hash = order[seat]
      if hash ~= nil then out[hash] = fingerprint(one) end
      if type(one) == "table" then
        for inner, deep in pairs(one) do
          local nested = order[inner]
          if nested ~= nil and out[nested] == nil then
            out[nested] = "n:" .. fingerprint(deep)
          end
        end
      end
    end
    return out
  end
  function Identity:switch_note(session, hand)
    local before = self.last_hand
    self.last_hand = (hand.goid ~= nil)
      and { goid = hand.goid, type = hand.type } or nil
    if before == nil or hand.goid == nil then return "" end
    if before.goid == hand.goid and before.type == hand.type then return "" end
    local head = " switch=" .. tostring(before.type) .. ":" ..
                 tostring(before.goid) .. "->" .. tostring(hand.type) .. ":" ..
                 tostring(hand.goid)
    if not self:live(session, before.goid) then
      return head .. " differ=old-gone"
    end
    local old, old_why = self:declared_values(session, before)
    local new, new_why = self:declared_values(session, hand)
    if old == nil or new == nil then
      return head .. " differ=unreadable old=" .. tostring(old_why or "ok") ..
             " new=" .. tostring(new_why or "ok")
    end
    local compared = 0
    local differ = {}
    for hash, base in pairs(old) do
      local other = new[hash]
      if other ~= nil then compared = compared + 1 end
      if other ~= nil and other ~= base then
        differ[#differ + 1] = hash .. "=" .. base .. "/" .. other
      end
    end
    table.sort(differ)
    return head .. " cells=" .. tostring(compared) .. " differ=" ..
           ((#differ == 0) and "same-in-every-cell"
             or table.concat(differ, ","))
  end
  function Identity:player_object(session, rows)
    local known = self.player_goid
    if known ~= nil then
      local still = call(self.counters, GS.game_object_is_type,
                         session, known, self.player.call)
      if still == true then
        self.counters.player_cached = (self.counters.player_cached or 0) + 1
        return { goid = known, type = self.player.type }, 1
      end
      self.player_goid = nil
    end
    local player, count = nil, 0
    for index = 1, #(rows or {}) do
      if rows[index].type == self.player.type then
        player = rows[index]
        count = count + 1
      end
    end
    if count == 1 then self.player_goid = player.goid end
    return player, count
  end
  function Identity:avatar_from(session, player)
    local field = self.player.avatar_field.call
    if field == nil then return nil, "no-authored-player-field" end
    local declared = self:declared_fields(player.type, self.player.call)
    if declared[self.player.avatar_field.hash or ""] ~= true then
      return nil, "player-does-not-declare-the-field"
    end
    local goid = call(self.counters, GS.game_object_field,
                     session, player.goid, field)
    if type(goid) ~= "number" then return nil, "player-field-unreadable" end
    if goid == NO_OBJECT then return nil, NO_AVATAR_NOW end
    return goid, nil
  end
  function Identity:in_control(session, avatar)
    local motion = self.avatar.motion_field
    if motion == nil or motion.call == nil then return nil end
    local declared = self:declared_fields(avatar.type, self.avatar.call)
    if declared[motion.hash or ""] ~= true then return nil end
    return call(self.counters, GS.game_object_field, session,
                avatar.goid, motion.call)
  end
  function Identity:avatar_value(session, avatar, spot)
    if spot == nil or spot.call == nil then return nil end
    local declared = self:declared_fields(avatar.type, self.avatar.call)
    if declared[spot.hash or ""] ~= true then return nil end
    return call(self.counters, GS.game_object_field, session,
                avatar.goid, spot.call)
  end
  function Identity:rotation_free(session, avatar)
    return self:avatar_value(session, avatar, self.avatar.rotation_field)
  end
  function Identity:gate_note(session, avatar, grip)
    local out = { ":grip=", tostring(grip) }
    for index = 1, #(self.avatar.gate_note_fields or {}) do
      local spot = self.avatar.gate_note_fields[index]
      out[#out + 1] = "-" .. tostring(spot.call) .. "=" ..
        tostring(self:avatar_value(session, avatar, spot))
    end
    return table.concat(out)
  end
  local function marks(list)
    local out = {}
    for index = 1, #list do
      if index > 6 then
        out[#out + 1] = "+" .. tostring(#list - 6) .. "-more"
        break
      end
      out[#out + 1] = tostring(list[index].type) .. ":" ..
                      tostring(list[index].goid) ..
                      ":mask=" .. tostring(list[index].mask)
    end
    return table.concat(out, ",")
  end
  function Identity:on_body_split(session, rows, pick)
    local kept, dropped, all = {}, {}, {}
    for index = 1, #rows do
      local candidate, call_string = pick(rows[index])
      if candidate ~= nil then
        all[#all + 1] = candidate
        local keep, _why, mask = self:on_my_body(session, candidate, call_string)
        candidate.mask = mask
        local into = keep and kept or dropped
        into[#into + 1] = candidate
      end
    end
    return kept, dropped, all
  end
  function Identity:find_backpack(session, rows)
    local kept, _dropped, owned = self:on_body_split(session, rows,
      function(row)
        local pack = self.backpacks[row.type]
        if pack == nil then return nil end
        return { goid = row.goid, type = row.type, call = pack.call }, pack.call
      end)
    if #owned == 0 then return nil, "no-owned-backpack" end
    if #kept == 1 then
      return kept[1], ((#owned == 1) and "sole-owned-backpack-on-my-body"
                        or "carry-bit-broke-the-tie") ..
                      " owned=" .. marks(owned)
    end
    if #kept == 0 then
      return nil, "owned-backpacks=" .. tostring(#owned) ..
                  " none-on-my-body tied=" .. marks(owned)
    end
    local pool = (#kept > 1) and kept or owned
    return nil, "owned-backpacks=" .. tostring(#owned) ..
                " on-body=" .. tostring(#kept) ..
                " tied=" .. marks(pool)
  end
  local function reason_head(reason)
    return type(reason) == "string" and reason:match("^[%a%-]+") or nil
  end
  local function empty_hand(status, reason, found)
    found = found or {}
    return {
      status = status, avatar = found.avatar, hand_weapon = nil,
      backpack = found.backpack, player = found.player, reason = reason,
      grip = found.grip,
      backpack_reason = found.backpack_reason or "not-resolved",
    }
  end
  local function tally(self, prefix, reason)
    local head = reason_head(reason)
    if head == nil then return end
    local key = prefix .. head:gsub("%-", "_")
    self.counters[key] = (self.counters[key] or 0) + 1
  end
  local function unknown(self, reason, found)
    self.counters.unknowns = self.counters.unknowns + 1
    tally(self, "unknown_", reason)
    return empty_hand(UNRESOLVED, reason, found)
  end
  local function absent(self, reason, found)
    self.counters.absents = self.counters.absents + 1
    tally(self, "absent_", reason)
    return empty_hand(ABSENT, reason, found)
  end
  function Identity:authored_read(session, hand, row)
    if hand.goid == nil then return nil, "no-hand-object" end
    if type(row) ~= "table" or type(row.call) ~= "string" then
      return nil, "no-authored-field"
    end
    local equipment = self.equipment[hand.type]
    if equipment == nil or type(equipment.call) ~= "string" then
      return nil, "no-call-for-hand-weapon"
    end
    local declared = self:declared_fields(hand.type, equipment.call)
    if declared[row.hash or ""] ~= true then
      return nil, "weapon-declares-no-field"
    end
    local value = call(self.counters, GS.game_object_field,
                       session, hand.goid, row.call)
    if type(value) ~= "number" then return nil, "unreadable" end
    return value
  end
  function Identity:mode_is_underbarrel(session, hand)
    local value = self:authored_read(session, hand, self.authored.mode_field)
    return value ~= nil and math.floor(value / 8) % 2 == 1
  end
  function Identity:find_underbarrel(session, hand)
    local goid, why =
      self:authored_read(session, hand, self.authored.underbarrel_field)
    if goid == nil then return nil, why end
    if goid == NO_OBJECT then return nil, "no-underbarrel" end
    if not self:live(session, goid) then return nil, "underbarrel-gone" end
    local type_hash = self:type_of(session, goid, nil)
    if type_hash == nil then
      type_hash = self:scan_types(session, goid, self.type_missed[goid] or {},
                                  "all", self:all_rows())
    end
    return { goid = goid, type = type_hash },
           type_hash ~= nil and "weapon-points-at-it"
           or "weapon-points-at-an-unnamed-object"
  end
  function Identity:resolve(session, world, my_peer)
    self.hand_unit = nil
    self.last_candidates = nil
    local missing = surface_missing()
    if missing ~= nil then
      return unknown(self, "surface-missing:" .. missing)
    end
    if session == nil or world == nil or my_peer == nil then
      return unknown(self, "no-session-world-or-peer")
    end
    self:sync(session, world)
    local grip, grip_reason = self:current_grip(world)
    local rows = nil
    local player, players = self:player_object(session, nil)
    if player == nil then
      rows = self:owned_objects(session, my_peer, grip)
      if rows == nil then return unknown(self, "owned-objects-unreadable") end
      player, players = self:player_object(session, rows)
    end
    if players ~= 1 then
      return unknown(self, "owned-players=" .. tostring(players))
    end
    local avatar_goid, avatar_reason = self:avatar_from(session, player)
    local found = {
      grip = grip,
      player = { goid = player.goid, type = self.player.type },
      avatar = avatar_goid ~= nil
        and { goid = avatar_goid, type = self.avatar.type } or nil,
    }
    if found.avatar == nil then
      local hand = (avatar_reason == NO_AVATAR_NOW) and absent or unknown
      return hand(self, "no-avatar:" .. tostring(avatar_reason), found)
    end
    if self:in_control(session, found.avatar) == false then
      if self:rotation_free(session, found.avatar) ~= false then
        return absent(self, "avatar-not-in-control" ..
                      self:gate_note(session, found.avatar, grip),
                      { grip = grip })
      end
      self.counters.gate_open_seated =
        (self.counters.gate_open_seated or 0) + 1
    end
    if grip == nil then
      return unknown(self, "no-grip:" .. tostring(grip_reason), found)
    end
    if grip == 0 then
      return absent(self, "grip=0-nothing-held", found)
    end
    rows = rows or self:owned_objects(session, my_peer, grip)
    if rows == nil then
      return unknown(self, "owned-objects-unreadable", found)
    end
    local backpack, backpack_reason = self:find_backpack(session, rows)
    found.backpack, found.backpack_reason = backpack, backpack_reason
    local candidates, dropped = self:on_body_split(session, rows,
      function(row)
        local equipment = self.equipment[row.type]
        if equipment == nil or equipment.grip ~= grip
           or equipment.no_hand then return nil end
        return { goid = row.goid, type = row.type, grip = equipment.grip },
               equipment.call
      end)
    for index = 1, #dropped do
      candidates[#candidates + 1] = dropped[index]
    end
    if #candidates == 0 then
      return absent(self, "no-on-body-object-of-grip=" .. tostring(grip) ..
                          " owned=" .. tostring(#rows) ..
                          " of_grip=" .. tostring(#dropped) ..
                          " dropped=" .. marks(dropped), found)
    end
    self.last_candidates = candidates
    local kinds, seen_kind = 0, {}
    for index = 1, #candidates do
      local kind = candidates[index].type
      if seen_kind[kind] == nil then
        seen_kind[kind] = true
        kinds = kinds + 1
      end
    end
    if kinds == 1 then
      self.counters.kinds_one = (self.counters.kinds_one or 0) + 1
    else
      self.counters.kinds_many = (self.counters.kinds_many or 0) + 1
    end
    local wield, held
    held, wield = self:narrow_by_wield(world, candidates)
    if #held == 0 then
      held = self:first_person_hand(world, candidates)
      wield = (#held > 0) and "first-person-node" or wield
    end
    if #held == 0 then
      held = self:carried_hand(world, candidates)
      wield = (#held > 0) and "carried" or wield
    end
    candidates = held
    if #candidates == 0 then
      if wield == "nothing-at-the-wield-node" then
        self.counters.wield_no_match =
          (self.counters.wield_no_match or 0) + 1
      end
      return unknown(self, "hand-empty:" .. tostring(wield) ..
                           " kinds=" .. tostring(kinds) ..
                           " objs=" .. tostring(#candidates) ..
                           " grip=" .. tostring(grip) ..
                           " dropped=" .. marks(dropped), found)
    end
    if #candidates > 1 then
      local on_body = {}
      for index = 1, #candidates do
        local mask = candidates[index].mask
        if type(mask) == "number" and mask % 4 == 0 then
          on_body[#on_body + 1] = candidates[index]
        end
      end
      if #on_body == 1 then
        self.counters.mask_broke_the_tie =
          (self.counters.mask_broke_the_tie or 0) + 1
        candidates = on_body
      end
    end
    if #candidates > 1 then
      return unknown(self, "on-body-objects=" .. tostring(#candidates) ..
                           " kinds=" .. tostring(kinds) ..
                           " grip=" .. tostring(grip) ..
                           " wield=" .. tostring(wield) ..
                           " tied=" .. marks(candidates), found)
    end
    local chosen = candidates[1]
    self.carried_goid = chosen.goid
    self.counters.resolves = self.counters.resolves + 1
    local hand = {
      goid = chosen.goid,
      type = chosen.type,
      grip = chosen.grip,
    }
    local underbarrel, underbarrel_reason = self:find_underbarrel(session, hand)
    if underbarrel ~= nil and underbarrel.goid ~= nil and
       self:mode_is_underbarrel(session, hand) then
      self.counters.mode_took_the_underbarrel =
        (self.counters.mode_took_the_underbarrel or 0) + 1
      hand = { goid = underbarrel.goid, type = underbarrel.type,
               grip = hand.grip, mask = hand.mask, was = chosen.type }
    end
    return {
      status = "resolved",
      player = found.player,
      account = self:account_of(session, hand),
      grip = grip,
      avatar = found.avatar,
      underbarrel = underbarrel,
      underbarrel_reason = underbarrel_reason,
      hand_weapon = hand,
      backpack = backpack,
      reason = "wield-node-named-the-hand:" .. tostring(wield) ..
               " kinds=" .. tostring(kinds) ..
               " mask=" .. tostring(chosen.mask) ..
               ((#dropped > 0) and (" dropped=" .. marks(dropped)) or "") ..
               self:switch_note(session, hand),
      backpack_reason = backpack_reason,
    }
  end
  function Identity:report()
    local out = {}
    for key, value in pairs(self.counters) do out[key] = value end
    out.cached_types = 0
    for _ in pairs(self.type_fields) do
      out.cached_types = out.cached_types + 1
    end
    return out
  end
  return Core
end)()
local __contract_ok_1, __contract_export_1 = pcall(function() return IdentityCore end)
if not __contract_ok_1 or __contract_export_1 == nil then error("20-identity-core: missing export IdentityCore", 0) end
return {["IdentityCore"]=__contract_export_1}
end
end)()
setfenv(__factory, __module_environment("20-identity-core", true))
local __exports = __module_run("20-identity-core", __factory, __imports, __module_log)
if __exports ~= nil then
__module_registry["IdentityCore"] = __exports["IdentityCore"]
end
end
do
local __imports = {}
local __factory = (function()
return function(imports)
if type(imports) ~= "table" then error("30-generated-common: imports must be a table", 0) end
local GeneratedCommon = {
  ["slot_keys"] = {
    ["right/arc.1"] = 1,
    ["right/arc.2"] = 2,
    ["right/icon.1"] = 3,
    ["right/number.1"] = 4,
    ["right/number.2"] = 5,
  },
  ["slot_owners"] = {
    ["right/arc.1"] = "hand-weapon",
    ["right/arc.2"] = "hand-weapon",
    ["right/icon.1"] = "hand-weapon",
    ["right/number.1"] = "hand-weapon",
    ["right/number.2"] = "hand-weapon",
  },
  ["guest_precedence"] = {
    "hand-weapon",
    "backpack",
    "avatar",
    "player",
  },
  ["layout"] = {
    ["style"] = {
      ["alpha_gain"] = 0.7,
      ["idle_ratio"] = 0,
      ["idle_fade_seconds"] = 2.5,
    },
    ["palette"] = {
      ["normal"] = {
        ["body"] = { 240, 240, 226 },
        ["ink"] = { 120, 120, 120 },
      },
      ["warn"] = {
        ["body"] = { 235, 140, 110 },
        ["ink"] = { 165, 98, 77 },
      },
    },
  },
  ["identity"] = {
    ["avatar"] = {
      ["type"] = "0x5d9752f9",
      ["call"] = "gl7xl2w",
      ["first_person"] = "content/fac_helldivers/fp_cha_avatar/fp_cha_avatar",
      ["third_person"] = "content/fac_helldivers/cha_avatar/avatar_helldiver",
      ["motion_field"] = {
        ["hash"] = "0xd49efb40",
        ["call"] = "motion_enabled",
      },
      ["rotation_field"] = {
        ["hash"] = "0x4441caf8",
        ["call"] = "rotation_enabled",
      },
      ["gate_note_fields"] = {
        {
          ["hash"] = "0xcb0453ea",
          ["call"] = "health",
        },
        {
          ["hash"] = "0x82830aed",
          ["call"] = "state",
        },
        {
          ["hash"] = "0x4441caf8",
          ["call"] = "rotation_enabled",
        },
      },
      ["grip_variable"] = "weapon_selected",
    },
    ["player"] = {
      ["type"] = "0x803159b3",
      ["call"] = "un6y1d",
      ["avatar_field"] = {
        ["hash"] = "0x0f27c68e",
        ["call"] = "baegche",
      },
    },
    ["carry_field"] = {
      ["hash"] = "0x788b9753",
      ["call"] = "enabled_mask",
    },
    ["account_field"] = {
      ["hash"] = "0x3ccffa7f",
      ["call"] = "original_owner",
    },
    ["bind_field"] = {
      ["hash"] = "0xc3a3f66c",
      ["call"] = "dvpupm",
    },
    ["underbarrel_field"] = {
      ["hash"] = "0x67526982",
      ["call"] = "underbarrel_goid",
    },
    ["mode_field"] = {
      ["hash"] = "0x49c250a6",
      ["call"] = "fire_mode",
    },
    ["equipment"] = {
      ["0xecbda4d6"] = {
        ["call"] = "hcjwx8i",
        ["grip"] = 41,
        ["resource"] = "content/fac_helldivers/equipment/throwables/flashbang/flashbang",
      },
      ["0x8c0aee6e"] = {
        ["call"] = "pcdv3w",
        ["grip"] = 51,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/grenade_launcher/grenade_launcher",
        ["holster_node"] = "support_mg",
        ["reload"] = {
          [0] = { 0, 1 },
        },
      },
      ["0xbe9b793e"] = {
        ["call"] = "njgfbx",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/marksman_rifle_vigilance/marksman_rifle_vigilance",
        ["holster_offset"] = 0.112805,
        ["reload"] = {
          [1] = { 0, 1 },
        },
      },
      ["0xf3d3ab0e"] = {
        ["call"] = "gmb_sfp",
        ["grip"] = 41,
        ["resource"] = "content/fac_helldivers/equipment/throwables/zipper_grenade/zipper_grenade",
      },
      ["0x5d2ad31e"] = {
        ["call"] = "ajrd8a",
        ["grip"] = 41,
        ["resource"] = "content/fac_helldivers/equipment/throwables/gas_grenade/gas_grenade",
      },
      ["0x83164c66"] = {
        ["call"] = "cchm4wf",
        ["grip"] = 41,
        ["resource"] = "content/fac_helldivers/equipment/throwables/incendiary_grenade/incendiary_grenade",
      },
      ["0x303d2220"] = {
        ["call"] = "fa656cj",
        ["grip"] = 80,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/pump_shotgun_plasma/pump_shotgun_plasma",
        ["holster_offset"] = 0.025,
        ["reload"] = {
          [1] = { 0 },
        },
      },
      ["0x4daea8e1"] = {
        ["call"] = "fsqqjdv",
        ["grip"] = 1,
        ["resource"] = "content/fac_helldivers/equipment/sidearm_weapons/standard_pistol/standard_pistol",
        ["holster_node"] = "pistol",
        ["reload"] = {
          [1] = { 1, 2, 3, 4 },
        },
      },
      ["0x5368c409"] = {
        ["call"] = "fa2o6wb",
        ["grip"] = 41,
        ["resource"] = "content/fac_helldivers/equipment/throwables/cluster_frag_grenade/cluster_frag_grenade",
      },
      ["0x33a9a66c"] = {
        ["call"] = "sdsikk",
        ["grip"] = 80,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/arc_shotgun/arc_shotgun",
        ["holster_offset"] = 0.15,
      },
      ["0x5590232e"] = {
        ["call"] = "a1nulw",
        ["grip"] = 70,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/personal_defense_weapon_pepper/personal_defense_weapon_pepper",
        ["holster_offset"] = 0.052202,
        ["reload"] = {
          [1] = { 0, 1, 2, 3 },
        },
      },
      ["0x0df6deff"] = {
        ["call"] = "hb_jjqp",
        ["grip"] = 51,
        ["resource"] = "content/fac_helldivers/vehicles/frv/armaments/frv_mg/frv_mg",
        ["holster_node"] = "support_mg",
        ["rof"] = {
          ["450"] = "rof_low",
          ["600"] = "rof_medium",
          ["750"] = "rof_high",
        },
        ["reload"] = {
          [0] = { 3, 4 },
        },
      },
      ["0x1d3b01d7"] = {
        ["call"] = "8d0hr5",
        ["grip"] = 70,
        ["resource"] = "content/fac_helldivers/equipment/sidearm_weapons/caustic_dart_gun/caustic_dart_gun",
        ["holster_node"] = "pistol",
        ["reload"] = {
          [1] = { 1, 2 },
        },
      },
      ["0xc0e5aa7f"] = {
        ["call"] = "egae12i",
        ["grip"] = 15,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/battle_rifle_ceremonial/battle_rifle_ceremonial",
        ["holster_offset"] = 0.025,
        ["reload"] = {
          [0] = { 4 },
        },
      },
      ["0xe645b6ee"] = {
        ["call"] = "htx7gy",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/machinegun/machinegun",
        ["holster_node"] = "support_mg",
        ["rof"] = {
          ["630"] = "rof_low",
          ["760"] = "rof_medium",
          ["900"] = "rof_high",
        },
        ["reload"] = {
          [1] = { 2, 3 },
        },
      },
      ["0xe86fb7d6"] = {
        ["call"] = "dboq3ts",
        ["grip"] = 41,
        ["resource"] = "content/fac_helldivers/equipment/throwables/dynamite/dynamite",
      },
      ["0xfd585726"] = {
        ["call"] = "e5j54r",
        ["grip"] = 1,
        ["resource"] = "content/fac_helldivers/equipment/sidearm_weapons/smart_pistol_missile/smart_pistol_missile",
        ["holster_node"] = "pistol",
        ["draws_ammo_mode"] = true,
        ["ammo_icon"] = {
          ["0"] = "guidance_on",
          ["1"] = "guidance_off",
        },
        ["reload"] = {
          [1] = { 1, 2 },
        },
      },
      ["0x69971ef9"] = {
        ["call"] = "gdciibt",
        ["grip"] = 70,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/smg_solvent/smg_solvent",
        ["holster_offset"] = 0.112805,
        ["reload"] = {
          [1] = { 0, 1, 2, 3 },
        },
      },
      ["0xc7dfd9eb"] = {
        ["call"] = "vdvv_d",
        ["grip"] = 1,
        ["resource"] = "content/fac_helldivers/equipment/sidearm_weapons/magnum_pistol/magnum_pistol",
        ["holster_node"] = "pistol",
        ["reload"] = {
          [1] = { 0, 1, 2, 3 },
        },
      },
      ["0xa6445802"] = {
        ["call"] = "fihemjs",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/marksman_rifle_shark/marksman_rifle_shark",
        ["holster_offset"] = 0.112805,
        ["reload"] = {
          [1] = { 2 },
        },
      },
      ["0x59044eaa"] = {
        ["call"] = "59an6f",
        ["grip"] = 40,
        ["resource"] = "content/fac_helldivers/equipment/throwables/energy_shield_grenade/energy_shield_grenade",
      },
      ["0x06a36e8b"] = {
        ["call"] = "glryahy",
        ["grip"] = 1,
        ["resource"] = "content/fac_helldivers/equipment/throwables/emp_grenade/emp_grenade",
      },
      ["0x9e18f0cf"] = {
        ["call"] = "znvy6p",
        ["grip"] = 51,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/heavy_mg/heavy_mg",
        ["holster_node"] = "support_mg",
        ["rof"] = {
          ["450"] = "rof_low",
          ["600"] = "rof_medium",
          ["750"] = "rof_high",
        },
        ["reload"] = {
          [1] = { 0, 1 },
        },
      },
      ["0x83d1036e"] = {
        ["call"] = "aax9fwn",
        ["grip"] = 20,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/faf_missile_launcher/faf_missile_launcher",
        ["holster_node"] = "support",
        ["spare_pack"] = {
          ["0x61ea0c42"] = true,
          ["0xb32572a6"] = true,
        },
        ["reload"] = {
          [0] = { 0, 1, 3 },
        },
      },
      ["0x8a3c6fee"] = {
        ["call"] = "atz1va",
        ["grip"] = 20,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/air_burst_rocket_launcher/air_burst_rocket_launcher",
        ["holster_node"] = "support",
        ["spare_pack"] = {
          ["0x816a11e0"] = true,
        },
        ["draws_ammo_mode"] = true,
        ["ammo_icon"] = {
          ["0"] = "ammo_flak",
          ["1"] = "ammo_frag",
        },
        ["reload"] = {
          [0] = { 0, 1, 3 },
        },
      },
      ["0x42a6b5ba"] = {
        ["call"] = "t24vbr",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/laser_rifle/laser_rifle",
        ["holster_offset"] = 0.057879,
        ["reload"] = {
          [0] = { 0 },
        },
      },
      ["0x960653cb"] = {
        ["call"] = "cf6b349",
        ["grip"] = 15,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/laser_rifle_long_hotshot/laser_rifle_long_hotshot",
        ["holster_offset"] = 0.147817,
        ["reload"] = {
          [0] = { 2 },
        },
      },
      ["0x5c661180"] = {
        ["call"] = "tjv7_5",
        ["grip"] = 40,
        ["resource"] = "content/fac_helldivers/equipment/throwables/caltrops_grenade/caltrops_grenade",
      },
      ["0x437d9eed"] = {
        ["call"] = "8l081y",
        ["grip"] = 5,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/triple_barrel_breakshotgun/triple_barrel_breakshotgun",
        ["holster_node"] = "pistol",
        ["draws_fire_mode"] = true,
        ["ammo_icon"] = {
          ["0"] = "ammo_buckshot",
        },
        ["reload"] = {
          [1] = { 0, 3 },
        },
      },
      ["0x66611cce"] = {
        ["call"] = "zvthn5",
        ["grip"] = 40,
        ["resource"] = "content/fac_helldivers/equipment/throwables/self_destruct_drone/self_destruct_drone",
        ["wield_offset"] = 0.05,
      },
      ["0x26acc614"] = {
        ["call"] = "bhy750",
        ["grip"] = 61,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/railgun/railgun",
        ["holster_node"] = "support_mg",
        ["draws_fire_mode"] = true,
        ["reload"] = {
          [1] = { 1 },
        },
      },
      ["0x64bb2549"] = {
        ["call"] = "cc_7z0n",
        ["grip"] = 1,
        ["resource"] = "0x64cdb52aca152be1",
        ["holster_node"] = "pistol",
      },
      ["0x6cc38f79"] = {
        ["call"] = "kur75r",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/sniper_rifle_helghast/sniper_rifle_helghast",
        ["holster_offset"] = 0.060208,
        ["reload"] = {
          [1] = { 0 },
        },
      },
      ["0x3e51c09b"] = {
        ["call"] = "j8kw9_",
        ["grip"] = 1,
        ["resource"] = "content/fac_helldivers/equipment/sidearm_weapons/automatic_pistol/automatic_pistol",
        ["holster_node"] = "pistol",
        ["reload"] = {
          [1] = { 1, 2, 3, 4 },
        },
      },
      ["0x366d3622"] = {
        ["call"] = "7xw2x3",
        ["grip"] = 20,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/laser_pulse_cannon/laser_pulse_cannon",
        ["holster_node"] = "support",
        ["cools_while_overheated"] = true,
      },
      ["0x96c78961"] = {
        ["call"] = "byktdb3",
        ["grip"] = 20,
        ["resource"] = "content/objectives/obj_common/shoulder_mounted_camera/shoulder_mounted_camera",
        ["holster_node"] = "support",
        ["cools_while_overheated"] = true,
      },
      ["0x2df95dfe"] = {
        ["call"] = "jq39fx",
        ["grip"] = 15,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/harpoon_gun/harpoon_gun",
        ["holster_node"] = "support_mg",
        ["reload"] = {
          [0] = { 3 },
        },
      },
      ["0xdef25ee4"] = {
        ["call"] = "ad8u70w",
        ["grip"] = 60,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/flamethrower/flamethrower",
        ["holster_offset"] = 0.403113,
        ["holster_node"] = "support_mg",
        ["reload"] = {
          [0] = { 4 },
        },
      },
      ["0x7dce85e3"] = {
        ["call"] = "mfjhk7",
        ["grip"] = 15,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/laser_shotgun/laser_shotgun",
        ["holster_offset"] = 0.147817,
        ["reload"] = {
          [0] = { 3 },
        },
      },
      ["0x0fe87029"] = {
        ["call"] = "baj_bt",
        ["grip"] = 5,
        ["resource"] = "content/fac_helldivers/equipment/sidearm_weapons/flamer_pistol/flamer_pistol",
        ["holster_node"] = "pistol",
        ["reload"] = {
          [0] = { 0, 1 },
        },
      },
      ["0x5ba7a298"] = {
        ["call"] = "gb3kptd",
        ["grip"] = 42,
        ["resource"] = "content/fac_helldivers/equipment/throwables/sticky_stun_grenade/sticky_stun_grenade",
      },
      ["0x4e87d68d"] = {
        ["call"] = "fo1q15s",
        ["grip"] = 9,
        ["resource"] = "content/fac_helldivers/equipment/sidearm_weapons/energy_revolver/energy_revolver",
        ["holster_node"] = "pistol",
        ["reload"] = {
          [0] = { 1, 2 },
        },
      },
      ["0xaca565f4"] = {
        ["call"] = "btdkfjn",
        ["grip"] = 80,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/pump_shotgun/pump_shotgun",
        ["holster_offset"] = 0.025,
        ["ammo_icon"] = {
          ["0"] = "ammo_buckshot",
          ["1"] = "ammo_buckshot",
        },
        ["reload"] = {
          [1] = { 3, 4, 5, 6, 7 },
        },
      },
      ["0xb3246bb7"] = {
        ["call"] = "0wjq8f",
        ["grip"] = 50,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/minigun/minigun",
        ["holster_offset"] = 0.180555,
        ["holster_node"] = "support_mg",
        ["ammo_pack"] = {
          ["0x32fd58bf"] = true,
        },
      },
      ["0xe42d5902"] = {
        ["call"] = "fa3glvx",
        ["grip"] = 90,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/assault_rifle_penetrator/assault_rifle_penetrator",
        ["holster_offset"] = 0.11225,
        ["reload"] = {
          [0] = { 1, 2 },
        },
      },
      ["0x66f5d393"] = {
        ["call"] = "acraszg",
        ["grip"] = 60,
        ["resource"] = "content/fac_helldivers/vehicles/frv_heavy/armaments/frv_flamethrower/frv_flamethrower",
        ["holster_offset"] = 0.403113,
        ["holster_node"] = "support_mg",
        ["reload"] = {
          [0] = { 3, 4 },
        },
      },
      ["0x6876b30a"] = {
        ["call"] = "aeksb9w",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/assault_shotgun/assault_shotgun",
        ["holster_offset"] = 0.043232,
        ["reload"] = {
          [0] = { 2, 3 },
        },
      },
      ["0x681b1f5f"] = {
        ["call"] = "affqw0h",
        ["grip"] = 42,
        ["resource"] = "content/fac_helldivers/equipment/throwables/at_explosive_grenade/at_explosive_grenade",
      },
      ["0x942a0601"] = {
        ["call"] = "7bkugd",
        ["grip"] = 70,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/smg_helghast/smg_helghast",
        ["holster_offset"] = 0.112805,
        ["reload"] = {
          [1] = { 0, 1, 2, 3 },
        },
      },
      ["0x6c26a65d"] = {
        ["call"] = "afyocuw",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/marksman_rifle_vigilance_counter_sniper/marksman_rifle_vigilance_counter_sniper",
        ["holster_offset"] = 0.112805,
        ["reload"] = {
          [1] = { 0, 1 },
        },
      },
      ["0xcc708c80"] = {
        ["call"] = "dbwlqks",
        ["grip"] = 41,
        ["resource"] = "content/fac_helldivers/equipment/throwables/frag_grenade/frag_grenade",
      },
      ["0xefa02c2f"] = {
        ["call"] = "cpojtx4",
        ["grip"] = 1,
        ["resource"] = "content/fac_helldivers/equipment/sidearm_weapons/pistol_nacho/pistol_nacho",
        ["holster_node"] = "pistol",
        ["reload"] = {
          [1] = { 1, 2, 3, 4 },
        },
      },
      ["0x06887ef8"] = {
        ["call"] = "bgevul2",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/assault_rifle_nacho/assault_rifle_nacho",
        ["holster_offset"] = 0.11225,
        ["reload"] = {
          [1] = { 1, 2 },
        },
      },
      ["0x9c06f074"] = {
        ["call"] = "fcvfq4z",
        ["grip"] = 80,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/pump_shotgun_02/pump_shotgun_02",
        ["holster_offset"] = 0.025,
        ["draws_magazine"] = true,
        ["ammo_icon"] = {
          ["0"] = "0x3b975896bc689499",
          ["1"] = "ammo_stun",
        },
        ["reload"] = {
          [1] = { 3, 4, 5, 6, 7 },
        },
      },
      ["0x11d8c98b"] = {
        ["call"] = "fa5p7ma",
        ["grip"] = 70,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/personal_defense_weapon/personal_defense_weapon",
        ["holster_offset"] = 0.052202,
        ["reload"] = {
          [1] = { 0, 1, 2, 3 },
        },
      },
      ["0x193ffddf"] = {
        ["call"] = "cf9mzkj",
        ["grip"] = 80,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/pump_shotgun_slug/pump_shotgun_slug",
        ["holster_offset"] = 0.025,
        ["ammo_icon"] = {
          ["0"] = "ammo_slug",
          ["1"] = "ammo_slug",
        },
        ["reload"] = {
          [1] = { 3, 4, 5, 6, 7 },
        },
      },
      ["0xa9e88c96"] = {
        ["call"] = "edev6ni",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/laser_rifle/laser_rifle",
        ["holster_node"] = "support_mg",
        ["reload"] = {
          [0] = { 0 },
        },
      },
      ["0x104184f7"] = {
        ["call"] = "xtkwjc",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/flamethrower_ripley/flamethrower_ripley",
        ["holster_offset"] = 0.25224,
        ["reload"] = {
          [0] = { 0 },
        },
      },
      ["0x6b2c3bed"] = {
        ["call"] = "kbv6zx",
        ["grip"] = 42,
        ["resource"] = "content/fac_helldivers/equipment/backpacks/c4_charge_backpack/c4_charge_detonator",
        ["holster_node"] = "target_designator",
        ["spare_pack"] = {
          ["0x66d03a73"] = true,
        },
        ["draws_charge"] = true,
        ["ammo_icon"] = {
          ["0"] = "0x38adabc6a32af014",
          ["1"] = "0x55374383474193f8",
        },
      },
      ["0x32fba243"] = {
        ["call"] = "ge4x7a0",
        ["grip"] = 15,
        ["resource"] = "content/fac_super_earth/equipment/support_weapons/shotgun_doublebarrel/shotgun_doublebarrel",
        ["holster_offset"] = 0.217256,
        ["holster_node"] = "support_mg",
        ["ammo_icon"] = {
          ["0"] = "ammo_buckshot",
        },
        ["reload"] = {
          [1] = { 0, 1 },
        },
      },
      ["0xdd7fc64f"] = {
        ["call"] = "805ydk",
        ["grip"] = 42,
        ["resource"] = "content/fac_helldivers/equipment/melee_weapons/electric_baton/electric_baton",
        ["holster_node"] = "pistol",
      },
      ["0x362577f7"] = {
        ["call"] = "af2403k",
        ["grip"] = 1,
        ["resource"] = "content/fac_helldivers/equipment/sidearm_weapons/grenade_pistol/grenade_pistol",
        ["holster_node"] = "pistol",
        ["reload"] = {
          [0] = { 0, 3 },
        },
      },
      ["0xeae34bee"] = {
        ["call"] = "1naimo",
        ["grip"] = 42,
        ["resource"] = "0x557ea199f0713919",
        ["holster_offset"] = 0.053852,
        ["holster_node"] = "attach_knife",
      },
      ["0x3d538455"] = {
        ["call"] = "db7vznz",
        ["grip"] = 20,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/laser_guided_missile_launcher/laser_guided_missile_launcher",
        ["holster_node"] = "support",
        ["draws_laser_guide"] = true,
      },
      ["0x3557a973"] = {
        ["call"] = "aeayb9y",
        ["grip"] = 41,
        ["resource"] = "content/fac_helldivers/equipment/throwables/molotov_grenade/molotov_grenade",
      },
      ["0x40ff7f06"] = {
        ["call"] = "ffc2lb",
        ["grip"] = 41,
        ["resource"] = "content/fac_helldivers/equipment/throwables/smoke_grenade/smoke_grenade",
      },
      ["0x1962a5a9"] = {
        ["call"] = "b8ayvi",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/assault_shotgun_sprayandpray/assault_shotgun_sprayandpray",
        ["holster_offset"] = 0.043232,
        ["reload"] = {
          [0] = { 2, 3 },
        },
      },
      ["0x756a4e4d"] = {
        ["call"] = "hbae32",
        ["grip"] = 50,
        ["resource"] = "content/fac_helldivers/equipment/melee_weapons/sledge_hammer/sledge_hammer",
        ["holster_offset"] = 0.333617,
        ["holster_node"] = "support_mg",
        ["reload"] = {
          [3] = { 1 },
        },
      },
      ["0x967a3fed"] = {
        ["call"] = "bi1lz0v",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/battle_rifle/battle_rifle",
        ["holster_offset"] = 0.060208,
        ["reload"] = {
          [1] = { 0, 1 },
        },
      },
      ["0x78949b7b"] = {
        ["call"] = "emedtvd",
        ["grip"] = 1,
        ["resource"] = "content/fac_helldivers/equipment/throwables/antitank_grenade/antitank_grenade",
      },
      ["0x55404d5c"] = {
        ["call"] = "dfrd64s",
        ["grip"] = 41,
        ["resource"] = "content/fac_helldivers/equipment/throwables/he_grenade/he_grenade",
      },
      ["0xf2c595ea"] = {
        ["call"] = "so5lnk",
        ["grip"] = 51,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/energy_weapon_shark/energy_weapon_shark",
        ["holster_offset"] = 0.11726,
        ["holster_node"] = "support_mg",
        ["reload"] = {
          [0] = { 2 },
        },
      },
      ["0x30b55bbc"] = {
        ["call"] = "cgp_im3",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/assault_rifle_whisper/assault_rifle_whisper",
        ["holster_offset"] = 0.112805,
        ["reload"] = {
          [1] = { 1, 2 },
        },
      },
      ["0x3f87a7e8"] = {
        ["call"] = "7pmh2p",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/shotgun_double_freedom/shotgun_double_freedom",
        ["draws_fire_mode"] = true,
        ["ammo_icon"] = {
          ["0"] = "ammo_buckshot",
        },
        ["reload"] = {
          [4] = { 1, 2 },
        },
      },
      ["0x0b9762b2"] = {
        ["call"] = "gen_fa0",
        ["grip"] = 42,
        ["resource"] = "content/fac_helldivers/equipment/melee_weapons/hand_axe/hand_axe",
        ["holster_offset"] = 0.035355,
        ["holster_node"] = "target_designator",
      },
      ["0xc0ff5282"] = {
        ["call"] = "b3vi17",
        ["grip"] = 20,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/expendable_massive_rocket_launcher/expendable_massive_rocket_launcher",
        ["holster_node"] = "support",
      },
      ["0x3ab8c79c"] = {
        ["call"] = "hqc00gk",
        ["grip"] = 41,
        ["resource"] = "content/fac_helldivers/equipment/throwables/impact_grenade/impact_grenade",
      },
      ["0x15f1a9f0"] = {
        ["call"] = "aacde2e",
        ["grip"] = 50,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/heavy_flamethrower/heavy_flamethrower",
        ["holster_offset"] = 0.403113,
        ["holster_node"] = "support_mg",
        ["ammo_pack"] = {
          ["0x6c17c3b1"] = true,
        },
        ["reload"] = {
          [0] = { 4 },
        },
      },
      ["0xc7448f32"] = {
        ["call"] = "thqhyt",
        ["grip"] = 42,
        ["resource"] = "content/fac_helldivers/equipment/melee_weapons/machete/machete",
        ["holster_offset"] = 0.117473,
        ["holster_node"] = "target_designator",
      },
      ["0xfeae4480"] = {
        ["call"] = "ff56xyl",
        ["grip"] = 1,
        ["resource"] = "content/fac_helldivers/equipment/sidearm_weapons/laser_pistol/laser_pistol",
        ["holster_node"] = "pistol",
        ["reload"] = {
          [0] = { 0, 1 },
        },
      },
      ["0xfc1ece86"] = {
        ["call"] = "oe283w",
        ["grip"] = 15,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/bolt_action_rifle/bolt_action_rifle",
        ["holster_offset"] = 0.025,
        ["reload"] = {
          [1] = { 0, 4 },
        },
      },
      ["0xf87a0104"] = {
        ["call"] = "3kjfa2",
        ["grip"] = 42,
        ["resource"] = "content/fac_helldivers/equipment/melee_weapons/survival_shovel/survival_shovel_trench",
        ["holster_offset"] = 0.351852,
        ["holster_node"] = "target_designator",
      },
      ["0xe3d29b1f"] = {
        ["call"] = "flwjtq",
        ["grip"] = 14,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/laser_rifle_charge/laser_rifle_charge",
        ["holster_offset"] = 0.057879,
        ["reload"] = {
          [0] = { 1 },
        },
      },
      ["0x6ec2a850"] = {
        ["call"] = "lh9psp",
        ["grip"] = 21,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/lat_oneshot/lat_oneshot",
        ["holster_node"] = "support",
      },
      ["0x82a8d3c2"] = {
        ["call"] = "ebbr5cc",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/jet_rifle/jet_rifle",
        ["holster_offset"] = 0.056789,
        ["reload"] = {
          [0] = { 3, 4, 5, 6, 7 },
        },
      },
      ["0x3bf357e5"] = {
        ["call"] = "cimj14q",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/assault_rifle_large_calibre_01/assault_rifle_large_calibre_01",
        ["reload"] = {
          [1] = { 1, 2 },
        },
      },
      ["0xd496b132"] = {
        ["call"] = "tt8fmz",
        ["grip"] = 15,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/laser_rifle_long/laser_rifle_long",
        ["holster_offset"] = 0.147817,
        ["reload"] = {
          [0] = { 2 },
        },
      },
      ["0x6b47e064"] = {
        ["call"] = "ddt_5lj",
        ["grip"] = 50,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/belt_fed_grenade_launcher/belt_fed_grenade_launcher",
        ["holster_offset"] = 0.180555,
        ["holster_node"] = "support_mg",
        ["ammo_pack"] = {
          ["0x139293f3"] = true,
        },
        ["rof"] = {
          ["160"] = "rof_low",
          ["240"] = "rof_medium",
          ["320"] = "rof_high",
        },
      },
      ["0x2ddd091d"] = {
        ["call"] = "cdoy79q",
        ["grip"] = 60,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/chemgun/chemgun",
        ["holster_offset"] = 0.5,
        ["holster_node"] = "support_mg",
        ["reload"] = {
          [0] = { 3 },
        },
      },
      ["0xb697955d"] = {
        ["call"] = "omhdnm",
        ["grip"] = 52,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/sniper_rifle/sniper_rifle",
        ["holster_node"] = "support_mg",
        ["reload"] = {
          [1] = { 0, 1 },
        },
      },
      ["0xcd0e4b4a"] = {
        ["call"] = "ptqxg5",
        ["grip"] = 70,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/smg_flamer/smg_flamer",
        ["holster_offset"] = 0.052202,
        ["draws_underbarrel"] = true,
        ["underbarrel_toggle"] = {
          ["0"] = "firemode_single",
          ["1"] = "firemode_underbarrel_flamer",
        },
        ["reload"] = {
          [1] = { 1, 2, 3, 4 },
        },
      },
      ["0xb397b9b3"] = {
        ["call"] = "gbas7ef",
        ["grip"] = 42,
        ["resource"] = "0x8d1f833f6a16de1a",
      },
      ["0x3b60cae4"] = {
        ["call"] = "45onxq",
        ["grip"] = 9,
        ["resource"] = "content/fac_helldivers/equipment/sidearm_weapons/revolver_pistol/revolver_pistol",
        ["holster_node"] = "pistol",
        ["reload"] = {
          [0] = { 2, 3, 4 },
          [2] = { 1, 2 },
          [4] = { 1 },
        },
      },
      ["0xb4a17af7"] = {
        ["call"] = "6zdquj",
        ["grip"] = 1,
        ["resource"] = "0x64cdb52aca152be1",
        ["holster_node"] = "support",
      },
      ["0x3478da46"] = {
        ["call"] = "y1uve6",
        ["grip"] = 80,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/shotgun_nacho/shotgun_nacho",
        ["holster_offset"] = 0.025,
        ["ammo_icon"] = {
          ["0"] = "ammo_buckshot",
        },
        ["reload"] = {
          [2] = { 1, 2 },
        },
      },
      ["0xab38cade"] = {
        ["call"] = "bhd28qa",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/smg_rhino/smg_rhino",
        ["holster_offset"] = 0.112805,
        ["reload"] = {
          [1] = { 0, 1 },
        },
      },
      ["0x22929ccb"] = {
        ["call"] = "m8qm65",
        ["grip"] = 70,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/smg_defender/smg_defender",
        ["holster_offset"] = 0.112805,
        ["reload"] = {
          [1] = { 0, 1, 2, 3 },
        },
      },
      ["0x8583e435"] = {
        ["call"] = "hh01pex",
        ["grip"] = 90,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/assault_rifle/assault_rifle",
        ["holster_offset"] = 0.11225,
        ["reload"] = {
          [0] = { 1, 2 },
        },
      },
      ["0x903649ec"] = {
        ["call"] = "aajirdm",
        ["grip"] = 51,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/arc_thrower/arc_thrower",
        ["holster_node"] = "support_mg",
      },
      ["0x3b2e3645"] = {
        ["call"] = "eb3ijks",
        ["grip"] = 42,
        ["resource"] = "content/fac_helldivers/equipment/backpacks/c4_charge_backpack/c4_charge",
      },
      ["0x36fcbd00"] = {
        ["call"] = "rl1ken",
        ["grip"] = 70,
        ["resource"] = "content/fac_helldivers/equipment/sidearm_weapons/pistol_cricket/pistol_cricket",
        ["holster_node"] = "pistol",
        ["reload"] = {
          [1] = { 0, 1, 2 },
        },
      },
      ["0xb2bfd847"] = {
        ["call"] = "mxfr0g",
        ["grip"] = 20,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/recoilless_rifle/recoilless_rifle",
        ["holster_node"] = "support",
        ["spare_pack"] = {
          ["0x066c9036"] = true,
          ["0x9f9ef431"] = true,
        },
        ["draws_ammo_mode"] = true,
        ["ammo_icon"] = {
          ["0"] = "ammo_heat",
          ["1"] = "ammo_he",
        },
        ["reload"] = {
          [1] = { 1, 2, 3 },
        },
      },
      ["0xffc5251f"] = {
        ["call"] = "jl0u_d",
        ["grip"] = 40,
        ["resource"] = "content/fac_helldivers/equipment/throwables/luring_mine/luring_mine",
      },
      ["0x521229d8"] = {
        ["call"] = "o6pa72",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/lmg_stalwart/lmg_stalwart",
        ["holster_node"] = "support_mg",
        ["rof"] = {
          ["700"] = "rof_low",
          ["850"] = "rof_medium",
          ["1150"] = "rof_high",
        },
        ["reload"] = {
          [0] = { 3, 4 },
        },
      },
      ["0x96c8cffd"] = {
        ["call"] = "lp1so",
        ["grip"] = 90,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/assault_rifle_patriot/assault_rifle_patriot",
        ["holster_offset"] = 0.112805,
        ["reload"] = {
          [2] = { 0, 1 },
        },
      },
      ["0x7bfb2e06"] = {
        ["call"] = "ea4wxj7",
        ["grip"] = 22,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/automatic_cannon/automatic_cannon",
        ["holster_node"] = "support",
        ["spare_pack"] = {
          ["0x94ff3b20"] = true,
          ["0xb9ea8c7e"] = true,
        },
        ["draws_ammo_mode"] = true,
        ["ammo_icon"] = {
          ["0"] = "0xf8ede5f922c05cb6",
          ["1"] = "ammo_flak",
        },
        ["reload"] = {
          [1] = { 1, 2, 3, 4, 5, 6 },
        },
      },
      ["0x3244dc49"] = {
        ["call"] = "na3ik9",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/assault_rifle_grenadier/assault_rifle_grenadier",
        ["holster_offset"] = 0.11225,
        ["draws_underbarrel"] = true,
        ["underbarrel_toggle"] = {
          ["0"] = "firemode_single",
          ["1"] = "firemode_underbarrel",
        },
        ["reload"] = {
          [1] = { 1, 2 },
        },
      },
      ["0x2605e68d"] = {
        ["call"] = "gec3v72",
        ["grip"] = 1,
        ["resource"] = "content/fac_helldivers/equipment/sidearm_weapons/plasma_pistol/plasma_pistol",
        ["holster_node"] = "pistol",
        ["reload"] = {
          [0] = { 1, 2 },
        },
      },
      ["0x5c813e43"] = {
        ["call"] = "ho_zkj",
        ["grip"] = 51,
        ["resource"] = "content/fac_helldivers/equipment/melee_weapons/flag/melee_flag",
        ["holster_offset"] = 0.070711,
        ["holster_node"] = "support_mg",
      },
      ["0x9c2e7f43"] = {
        ["call"] = "khpdlp",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/expendable_machinegun/expendable_machinegun",
        ["holster_offset"] = 0.028284,
        ["holster_node"] = "support_mg",
      },
      ["0xd724a2f9"] = {
        ["call"] = "v9tes",
        ["grip"] = 20,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/expendable_napalm_launcher/expendable_napalm_launcher",
        ["holster_node"] = "support",
      },
      ["0x15ae0889"] = {
        ["call"] = "bhp41hs",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/jet_rifle_phoenix/jet_rifle_phoenix",
        ["holster_offset"] = 0.056789,
        ["reload"] = {
          [0] = { 3, 4, 5, 6, 7 },
        },
      },
      ["0xfbc7f2e1"] = {
        ["call"] = "so8utv",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/assault_rifle_karbin/assault_rifle_karbin",
        ["holster_offset"] = 0.11225,
        ["reload"] = {
          [1] = { 1, 2 },
        },
      },
      ["0xd5e0448b"] = {
        ["call"] = "hbtp4i",
        ["grip"] = 80,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/pump_shotgun/pump_shotgun",
        ["holster_offset"] = 0.025,
        ["ammo_icon"] = {
          ["0"] = "ammo_buckshot",
          ["1"] = "ammo_buckshot",
        },
        ["reload"] = {
          [1] = { 3, 4, 5, 6, 7 },
        },
      },
      ["0x56c0a062"] = {
        ["call"] = "hazcaqu",
        ["grip"] = 9,
        ["resource"] = "content/fac_helldivers/equipment/sidearm_weapons/revolver_pistol_long/revolver_pistol_long",
        ["holster_node"] = "pistol",
        ["reload"] = {
          [0] = { 2, 3, 4 },
          [2] = { 1, 2 },
          [4] = { 1 },
        },
      },
      ["0x4321fb69"] = {
        ["call"] = "v36fp0",
        ["grip"] = 70,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/smg_nacho/smg_nacho",
        ["holster_offset"] = 0.112805,
        ["reload"] = {
          [1] = { 1, 2, 3, 4 },
        },
      },
      ["0x57079738"] = {
        ["call"] = "fjupfu7",
        ["grip"] = 51,
        ["resource"] = "content/fac_helldivers/equipment/melee_weapons/chainsaw_greatsword/chainsaw_greatsword",
        ["holster_offset"] = 0.229129,
        ["holster_node"] = "support_mg",
      },
      ["0xb30f446f"] = {
        ["call"] = "izfrrb6",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/assault_shotgun_incendiary/assault_shotgun_incendiary",
        ["holster_offset"] = 0.043232,
        ["reload"] = {
          [0] = { 2, 3 },
        },
      },
      ["0x00644350"] = {
        ["call"] = "eaojmhx",
        ["grip"] = 42,
        ["resource"] = "content/fac_helldivers/equipment/throwables/sticky_grenade/sticky_grenade",
      },
      ["0x2ac9b588"] = {
        ["call"] = "zjqdu0",
        ["grip"] = 1,
        ["resource"] = "content/fac_helldivers/equipment/sidearm_weapons/pistol_broomhandle/pistol_broomhandle",
        ["holster_node"] = "pistol",
        ["reload"] = {
          [2] = { 1, 2 },
        },
      },
      ["0x656d507b"] = {
        ["call"] = "_fqbgp",
        ["grip"] = 51,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/faf_missile_launcher_helghast/faf_missile_launcher_helghast",
        ["holster_offset"] = 0.291129,
        ["holster_node"] = "support",
        ["spare_pack"] = {
          ["0x764f37a0"] = true,
        },
        ["draws_ammo_mode"] = true,
        ["ammo_icon"] = {
          ["0"] = "burst_fire",
          ["1"] = "airstrike",
        },
        ["reload"] = {
          [0] = { 0, 1, 3 },
        },
      },
      ["0x1fffb81f"] = {
        ["call"] = "edkw7np",
        ["grip"] = 51,
        ["resource"] = "content/fac_helldivers/vehicles/frv/armaments/frv_mg/frv_mg",
        ["holster_node"] = "support_mg",
        ["rof"] = {
          ["450"] = "rof_low",
          ["600"] = "rof_medium",
          ["750"] = "rof_high",
        },
        ["reload"] = {
          [0] = { 3, 4 },
        },
      },
      ["0x2cef5e3b"] = {
        ["call"] = "ab81k_m",
        ["grip"] = 90,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/assault_rifle_helghast/assault_rifle_helghast",
        ["holster_offset"] = 0.11225,
        ["reload"] = {
          [0] = { 3, 4 },
        },
      },
      ["0x5f3517bf"] = {
        ["call"] = "0yw4c4",
        ["grip"] = 90,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/assault_rifle_rico/assault_rifle_rico",
        ["holster_offset"] = 0.112802,
        ["draws_rof"] = true,
        ["rof"] = {
          ["600"] = "rof_medium",
          ["850"] = "rof_high",
        },
        ["reload"] = {
          [1] = { 0, 1 },
        },
      },
      ["0xfe760204"] = {
        ["call"] = "hxop0h3",
        ["grip"] = 90,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/assault_rifle_explosive/assault_rifle_explosive",
        ["holster_offset"] = 0.11225,
        ["reload"] = {
          [0] = { 1, 2 },
        },
      },
      ["0xc920c2d3"] = {
        ["call"] = "9eomvl",
        ["grip"] = 1,
        ["resource"] = "content/fac_helldivers/equipment/sidearm_weapons/smart_pistol/smart_pistol",
        ["holster_node"] = "pistol",
        ["draws_ammo_mode"] = true,
        ["ammo_icon"] = {
          ["0"] = "guidance_on",
          ["1"] = "guidance_off",
        },
        ["reload"] = {
          [2] = { 1, 2, 3, 4 },
        },
      },
      ["0xbd5f17dd"] = {
        ["call"] = "begj2d5",
        ["grip"] = 80,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/pump_shotgun_dragon/pump_shotgun_dragon",
        ["holster_offset"] = 0.025,
        ["reload"] = {
          [1] = { 3, 4, 5, 6, 7 },
        },
      },
      ["0xd100e0c8"] = {
        ["call"] = "et8bo46",
        ["grip"] = 20,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/laser_cannon/laser_cannon",
        ["holster_node"] = "support",
        ["reload"] = {
          [1] = { 0, 1 },
        },
      },
      ["0xb4ac0dfa"] = {
        ["call"] = "eapufbs",
        ["grip"] = 1,
        ["resource"] = "content/fac_helldivers/equipment/sidearm_weapons/stim_pistol_01/stim_pistol",
        ["holster_node"] = "pistol",
        ["reload"] = {
          [0] = { 0, 1, 2 },
          [2] = { 0 },
          [4] = { 0 },
        },
      },
      ["0x6c76ed6e"] = {
        ["call"] = "bavxn9g",
        ["grip"] = 41,
        ["resource"] = "content/fac_helldivers/equipment/throwables/impact_smoke_grenade/impact_smoke_grenade",
      },
      ["0xff18dcb2"] = {
        ["call"] = "w114qw",
        ["grip"] = 41,
        ["resource"] = "content/fac_helldivers/equipment/throwables/arc_grenade/arc_grenade",
      },
      ["0x1220e497"] = {
        ["call"] = "clgsa3r",
        ["grip"] = 1,
        ["resource"] = "content/fac_helldivers/equipment/sidearm_weapons/pistol_shark/pistol_shark",
        ["holster_node"] = "pistol",
        ["reload"] = {
          [1] = { 1, 2, 3, 4 },
        },
      },
      ["0x5e0cd6f6"] = {
        ["call"] = "frnsjl",
        ["grip"] = 15,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/pump_shotgun_trench/pump_shotgun_trench",
        ["holster_offset"] = 0.025,
        ["ammo_icon"] = {
          ["0"] = "ammo_buckshot",
        },
        ["reload"] = {
          [1] = { 1, 2 },
        },
      },
      ["0xa6e06a41"] = {
        ["call"] = "cufoss",
        ["grip"] = 51,
        ["resource"] = "content/fac_helldivers/equipment/melee_weapons/stun_spear/stun_spear",
        ["holster_offset"] = 0.064031,
        ["holster_node"] = "target_designator",
      },
      ["0x9f16a5a2"] = {
        ["call"] = "aip6qqj",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/marksman_rifle_drake/marksman_rifle_drake",
        ["holster_offset"] = 0.112805,
        ["reload"] = {
          [1] = { 1, 2 },
        },
      },
      ["0xf18d75e5"] = {
        ["call"] = "bapxy3v",
        ["grip"] = 15,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/lever_action_rifle_01/lever_action_rifle_01",
        ["holster_offset"] = 0.025,
        ["reload"] = {
          [1] = { 1, 2 },
          [2] = { 1, 2, 3, 4 },
        },
      },
      ["0x8696c4f0"] = {
        ["call"] = "fe05wms",
        ["grip"] = 42,
        ["resource"] = "content/fac_helldivers/equipment/melee_weapons/survival_shovel/survival_shovel",
        ["holster_offset"] = 0.25671,
        ["holster_node"] = "support",
      },
      ["0x314a1bf5"] = {
        ["call"] = "hg8xdoa",
        ["grip"] = 20,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/plasma_blaster/plasma_blaster",
        ["holster_offset"] = 0.141421,
        ["holster_node"] = "support",
        ["reload"] = {
          [3] = { 1 },
        },
      },
      ["0x5aa66d27"] = {
        ["call"] = "gl_we0u",
        ["grip"] = 41,
        ["resource"] = "content/fac_helldivers/equipment/throwables/incendiary_impact_grenade/incendiary_impact_grenade",
      },
      ["0xca367ec7"] = {
        ["call"] = "fpnm922",
        ["grip"] = 42,
        ["resource"] = "content/fac_helldivers/equipment/throwables/mine_shark/mine_shark",
        ["no_hand"] = true,
      },
      ["0xa41ddfcd"] = {
        ["call"] = "fgz925w",
        ["grip"] = 14,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/plasma_rifle/plasma_rifle",
        ["holster_offset"] = 0.132382,
        ["reload"] = {
          [2] = { 0 },
        },
      },
      ["0x3b718900"] = {
        ["call"] = "be72pkz",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/marksman_rifle_justice/marksman_rifle_justice",
        ["reload"] = {
          [1] = { 1, 2 },
        },
      },
      ["0xeb38d34e"] = {
        ["call"] = "bba3bwo",
        ["grip"] = 70,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/crossbow_greyfax/crossbow_greyfax",
        ["holster_offset"] = 0.071414,
        ["reload"] = {
          [2] = { 0, 1 },
        },
      },
      ["0x6c9aabd3"] = {
        ["call"] = "aoazs6l",
        ["grip"] = 42,
        ["resource"] = "content/fac_helldivers/equipment/throwables/throwing_knife/throwing_knife",
        ["wield_offset"] = 0.1,
      },
      ["0xcfaa9ddc"] = {
        ["call"] = "cad9288",
        ["grip"] = 10,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/volley_gun/volley_gun",
        ["holster_offset"] = 0.072284,
        ["draws_fire_mode"] = true,
        ["rof"] = {
          ["300"] = "rof_low",
          ["550"] = "rof_medium",
          ["750"] = "rof_high",
        },
        ["reload"] = {
          [1] = { 1 },
        },
      },
      ["0xba161726"] = {
        ["call"] = "w6vav9",
        ["grip"] = 14,
        ["resource"] = "content/fac_helldivers/equipment/primary_weapons/plasma_rifle_charge/plasma_rifle_charge",
        ["holster_offset"] = 0.132382,
        ["reload"] = {
          [2] = { 0 },
        },
      },
      ["0x913cc553"] = {
        ["call"] = "eokgqmp",
        ["grip"] = 1,
        ["resource"] = "0x64cdb52aca152be1",
        ["holster_node"] = "target_designator",
      },
      ["0x48643cfd"] = {
        ["call"] = "anefmq",
        ["grip"] = 42,
        ["resource"] = "content/fac_helldivers/equipment/melee_weapons/ceremonial_saber/ceremonial_saber",
        ["holster_offset"] = 0.053852,
        ["holster_node"] = "target_designator",
      },
      ["0xe1f10066"] = {
        ["call"] = "x4eez5",
        ["grip"] = 51,
        ["resource"] = "content/fac_helldivers/equipment/support_weapons/grenade_launcher_tactical/grenade_launcher_tactical",
        ["holster_node"] = "support_mg",
        ["reload"] = {
          [1] = { 1, 2 },
        },
      },
    },
    ["underbarrels"] = {
      ["0x2166c7a1"] = {
        ["call"] = "da_b2us",
        ["icon"] = "firemode_underbarrel_flamer",
      },
      ["0x16425925"] = {
        ["call"] = "fc04pav",
        ["icon"] = "firemode_underbarrel",
      },
    },
    ["fire_mode_icons"] = {
      ["1"] = "firemode_auto",
      ["2"] = "firemode_single",
      ["3"] = "firemode_burst",
      ["4"] = "firemode_volley",
      ["7"] = "firemode_all_barrels",
      ["5"] = "firemode_single",
      ["6"] = "firemode_burst",
    },
    ["laser_icons"] = {
      ["0"] = "laser_off",
      ["false"] = "laser_off",
      ["1"] = "laser_on",
      ["true"] = "laser_on",
    },
    ["equipment_defaults"] = {
      ["wield_node"] = "attach_hand_r",
      ["wield_offset"] = 0.0,
      ["holster_node"] = "sling",
      ["holster_offset"] = 0.0,
    },
    ["icon_cells"] = {
      ["ammo_he"] = 0,
      ["0x081dcaf42dc2ecbb"] = 0,
      ["firemode_burst"] = 1,
      ["0x131742904d846798"] = 1,
      ["firemode_volley"] = 2,
      ["0x16413ee2e6725687"] = 2,
      ["ammo_stun"] = 3,
      ["0x2d9268907fb9420e"] = 3,
      ["firemode_deploy_c4"] = 4,
      ["0x38adabc6a32af014"] = 4,
      ["0x3b975896bc689499"] = 5,
      ["firemode_single"] = 6,
      ["0x3efff09cd12fb89a"] = 6,
      ["ammo_heat"] = 7,
      ["0x4b867645db9f364e"] = 7,
      ["rof_low"] = 8,
      ["0x52bbfc5a70359393"] = 8,
      ["firemode_detonate_c4"] = 9,
      ["0x55374383474193f8"] = 9,
      ["firemode_auto"] = 10,
      ["0x6115051c9558c50e"] = 10,
      ["burst_fire"] = 11,
      ["0x62d0512daebb0be0"] = 11,
      ["guidance_on"] = 12,
      ["0x7db70e669d8034c9"] = 12,
      ["ammo_buckshot"] = 13,
      ["0x8d197bfb4f7e95fc"] = 13,
      ["ammo_frag"] = 14,
      ["0x8dc0ec9ce217ab7f"] = 14,
      ["ammo_flak"] = 15,
      ["0x9dcc0312a14e11e6"] = 15,
      ["laser_off"] = 16,
      ["0xa9f9bf81827c51e5"] = 16,
      ["firemode_all_barrels"] = 17,
      ["0xad2ec8830fa5a193"] = 17,
      ["firemode_underbarrel"] = 18,
      ["0xb019df1ca8b6d4a2"] = 18,
      ["ammo_slug"] = 19,
      ["0xb7812fa260a5f64f"] = 19,
      ["guidance_off"] = 20,
      ["0xbc7975e05284cf57"] = 20,
      ["laser_on"] = 21,
      ["0xc1028550c62fc4fa"] = 21,
      ["rof_medium"] = 22,
      ["0xc7546fc9c7b6db4e"] = 22,
      ["firemode_underbarrel_flamer"] = 23,
      ["0xcd87fdddec2a6bfc"] = 23,
      ["rof_high"] = 24,
      ["0xf03c671b15d01a6b"] = 24,
      ["airstrike"] = 25,
      ["0xf83f6f461ed94e4b"] = 25,
      ["0xf8ede5f922c05cb6"] = 26,
    },
    ["atlas_columns"] = 16,
    ["atlas_rows"] = 2,
    ["backpacks"] = {
      ["0x32fd58bf"] = {
        ["call"] = "raqnyi",
        ["capacity"] = 1000,
      },
      ["0x0ab1da38"] = {
        ["call"] = "yehsgn",
      },
      ["0x066c9036"] = {
        ["call"] = "gyo1rh",
      },
      ["0xee341a1b"] = {
        ["call"] = "db5eb5l",
      },
      ["0x94ff3b20"] = {
        ["call"] = "aagn11w",
      },
      ["0x66d03a73"] = {
        ["call"] = "dgc0kc1",
      },
      ["0xa42ace6a"] = {
        ["call"] = "lnlden",
      },
      ["0x61ea0c42"] = {
        ["call"] = "eby5dep",
      },
      ["0x6c17c3b1"] = {
        ["call"] = "gm_1w9",
        ["capacity"] = 500,
      },
      ["0x447380d4"] = {
        ["call"] = "ccelgp4",
      },
      ["0xb32572a6"] = {
        ["call"] = "4h2oep",
      },
      ["0x9f9ef431"] = {
        ["call"] = "kfc4b8",
      },
      ["0xe866a492"] = {
        ["call"] = "fccswen",
      },
      ["0xb5b092f9"] = {
        ["call"] = "fblrz4",
      },
      ["0x764f37a0"] = {
        ["call"] = "ai7gl9n",
      },
      ["0x139293f3"] = {
        ["call"] = "feiw14g",
        ["capacity"] = 120,
      },
      ["0x42eb8b5a"] = {
        ["call"] = "ep18l48",
      },
      ["0xe80c471f"] = {
        ["call"] = "d7139m",
      },
      ["0xc4bf68fd"] = {
        ["call"] = "fdxo9sr",
      },
      ["0xb9ea8c7e"] = {
        ["call"] = "ghnne7l",
      },
      ["0x816a11e0"] = {
        ["call"] = "oxgf3v",
      },
      ["0x24bfe86e"] = {
        ["call"] = "3m6xaj",
      },
    },
    ["action"] = {
      ["layer"] = 3,
      ["gate"] = "weapon-rig",
    },
  },
  ["axes"] = {
    {
      ["id"] = "gauge",
      ["resource"] = "mods/hd2_hud/frag_gauge",
    },
    {
      ["id"] = "numbers",
      ["resource"] = "mods/hd2_hud/frag_numbers",
    },
    {
      ["id"] = "icon",
      ["resource"] = "mods/hd2_hud/frag_icon",
    },
    {
      ["id"] = "idle",
      ["resource"] = "mods/hd2_hud/frag_idle",
    },
  },
}
local __contract_ok_1, __contract_export_1 = pcall(function() return GeneratedCommon end)
if not __contract_ok_1 or __contract_export_1 == nil then error("30-generated-common: missing export GeneratedCommon", 0) end
return {["GeneratedCommon"]=__contract_export_1}
end
end)()
setfenv(__factory, __module_environment("30-generated-common", false))
local __exports = __module_run("30-generated-common", __factory, __imports, __module_log)
if __exports ~= nil then
__module_registry["GeneratedCommon"] = __exports["GeneratedCommon"]
end
end
do
local __imports = {}
local __factory = (function()
return function(imports)
if type(imports) ~= "table" then error("40-provider: imports must be a table", 0) end
local Provider = (function()
  local Core = { VERSION = "provider-v1" }
  local sr = rawget(_G, "stingray")
  local GS = sr and rawget(sr, "GameSession") or nil
  local Net = sr and rawget(sr, "Network") or nil
  local RELATION_SLOT = {
    ["hand-weapon"] = "hand_weapon",
    ["avatar"] = "avatar",
    ["backpack"] = "backpack",
    ["player"] = "player",
  }
  local function callable(namespace, name)
    return type(namespace) == "table" and
           type(rawget(namespace, name)) == "function"
  end
  local function pad16(value, width)
    local text = string.lower(tostring(value))
    if string.sub(text, 1, 2) == "0x" then text = string.sub(text, 3) end
    if #text < width then text = string.rep("0", width - #text) .. text end
    return text
  end
  local Instance = {}
  Instance.__index = Instance
  function Core.new(generated)
    local self = setmetatable({}, Instance)
    self.generated = generated or {}
    self.columns = self.generated.snapshot or {}
    self.fields = self.generated.network_fields or {}
    self.identity = self.generated.identity or {}
    self.signals = self.generated.signals or {}
    self.types_seen = {}
    self.role_tables = self.generated.role_tables or {}
    self.authored_base = self.generated.authored_base or {}
    self.authored_delta = self.generated.authored_delta or {}
    self.equipment = self.identity.equipment or {}
    self.packs = self.identity.backpacks or {}
    self.underbarrels = self.identity.underbarrels or {}
    self.slots = {}
    self.declared_note = {}
    self.counters = {
      windows = 0, batched_calls = 0, batched_errors = 0,
      columns_filled = 0, columns_missing = 0,
      relations_read = 0, relations_skipped = 0,
      declaration_calls = 0, declaration_errors = 0,
      declaration_types = 0, declaration_fields = 0,
      no_call_string = 0, hash_not_declared = 0,
      nested_reads = 0,
      delta_lookups = 0, delta_hits = 0, delta_misses = 0,
      delta_ambiguous = 0, signals_filled = 0,
      exists_misses = 0,
    }
    self.out = nil
    self.out_generation = nil
    self.hashes = {}
    return self
  end
  function Instance:field(signal, role)
    local row = self.signals[signal]
    if row == nil then return nil end
    return (self.role_tables[row.roles] or {})[role]
  end
  function Instance:cell_of(reads, signal, role)
    local spec = self:field(signal, role)
    local relation = (self.signals[signal] or {}).relation
    local hash = spec
    if type(spec) == "table" then
      hash = spec.field
      relation = spec.relation or relation
    end
    local read = reads[relation]
    if read == nil then return nil, false end
    if hash == nil then return nil, true end
    return read[hash], true
  end
  function Instance:raised(identity_result, column, relation)
    local hand = identity_result and identity_result.hand_weapon or nil
    local row = hand ~= nil and hand.type ~= nil
      and self.equipment[hand.type] or nil
    local want = row ~= nil and row[column] or nil
    if relation ~= "backpack" then
      return want ~= nil
    end
    local pack = identity_result and identity_result.backpack or nil
    return not (want ~= nil and pack ~= nil and pack.type ~= nil and
                want[pack.type] == true)
  end
  function Instance:overridden(name, reads, identity_result, entry)
    local row = self.signals[name] or {}
    local slot = RELATION_SLOT[entry.relation or row.relation]
    local named = slot ~= nil and identity_result[slot] or nil
    if named == nil or named.type == nil then return nil end
    local value = ((self.authored_base[entry.authored] or {})[named.type])
    if type(value) == "table" then
      local at = entry.from ~= nil and
        self:cell_of(reads, name, entry.from) or nil
      if type(at) ~= "number" then return nil end
      value = value[at + 1]
    end
    if entry.delta_from ~= nil then
      local array = self:cell_of(reads, name, entry.delta_from)
      local written = self.authored_delta[entry.authored] or {}
      if type(array) == "table" then
        local found, hits = nil, 0
        for _, raw in pairs(array) do
          local hit = written[raw]
          if hit == nil then
            hit = written["0x" .. pad16(raw, 16)]
          end
          if hit == nil and type(raw) == "string" then
            hit = written["0x" .. string.lower(raw)]
          end
          if hit ~= nil then
            hits = hits + 1
            found = hit
          end
        end
        self.counters.delta_lookups = self.counters.delta_lookups + 1
        if hits == 1 then
          self.counters.delta_hits = self.counters.delta_hits + 1
          value = found
        elseif hits == 0 then
          self.counters.delta_misses = self.counters.delta_misses + 1
        else
          self.counters.delta_ambiguous = self.counters.delta_ambiguous + 1
          return nil
        end
      end
    end
    if type(value) ~= "number" or value <= 0 then return nil end
    return value
  end
  local function labelled(names, at, slot, fallback)
    if type(names) ~= "table" then return nil end
    if slot ~= nil and type(at) == "number" then
      at = math.floor(at / (4 ^ slot)) % 4
    end
    if at == nil then
      if not fallback then return nil end
      at = 0
    end
    return names[tostring(at)]
  end
  function Instance:from_sources(name, reads, identity_result)
    local sources = (self.signals[name] or {}).sources
    if sources == nil then return nil, false end
    for index = 1, #sources do
      local entry = sources[index]
      local cell = entry
      local gated = type(entry) == "table" and entry.unless_authored ~= nil
        and self:raised(identity_result, entry.unless_authored,
                        (self.signals[name] or {}).relation)
      if not gated and type(entry) == "table" and entry.needs ~= nil then
        gated = not self:raised(identity_result, entry.needs,
                                (self.signals[name] or {}).relation)
      end
      if gated then
        cell = nil
      elseif type(entry) == "table" and entry.authored ~= nil then
        local value = self:overridden(name, reads, identity_result, entry)
        if value ~= nil then return value, true end
        cell = nil
      elseif type(entry) == "table" and entry.table ~= nil then
        local value = labelled((self.identity or {})[entry.table],
                               self:cell_of(reads, name, entry.from),
                               entry.slot, false)
        if value ~= nil then return value, true end
        cell = nil
      elseif type(entry) == "table" and entry.equipment ~= nil then
        local hand = (identity_result or {}).hand_weapon
        local row = hand ~= nil and hand.type ~= nil
          and self.equipment[hand.type] or nil
        local at, was_read = self:cell_of(reads, name, entry.from)
        local value = labelled(
          row ~= nil and row[entry.equipment] or nil,
          at, entry.slot, was_read)
        if value ~= nil then return value, true end
        cell = nil
      elseif type(entry) == "table" and entry.underbarrel ~= nil then
        local hand = (identity_result or {}).hand_weapon
        local row = hand ~= nil and hand.type ~= nil
          and self.underbarrels[hand.type] or nil
        local value = row ~= nil and row[entry.underbarrel] or nil
        if value ~= nil then return value, true end
        cell = nil
      elseif type(entry) == "table" and entry.pack ~= nil then
        local pack = (identity_result or {}).backpack
        local row = pack ~= nil and pack.type ~= nil
          and self.packs[pack.type] or nil
        local value = row ~= nil and row[entry.pack] or nil
        if value ~= nil then return value, true end
        cell = nil
      elseif type(entry) == "table" then
        cell = entry.from
        if entry.by ~= nil then
          local at = self:cell_of(reads, name, entry.from)
          cell = nil
          if type(at) == "number" then cell = entry.by[at + 1] end
          if cell == nil and at ~= nil then
            cell = entry.by[tostring(at)]
          end
        end
      end
      local value = nil
      if type(cell) == "number" then
        value = cell
      elseif cell ~= nil then
        if cell ~= name and self.signals[cell] ~= nil then
          value = self:signal(cell, reads, identity_result)
        else
          value = self:cell_of(reads, name, cell)
        end
      end
      if value ~= nil then return value, true end
    end
    return nil, true
  end
  function Instance:plus(name, reads, base, identity_result)
    local row = (self.signals[name] or {}).plus
    if row == nil then return base end
    local value = self:cell_of(reads, name, row.from)
    if value == nil then return base end
    local add = (value == false) and (row.when_false or 0) or 0
    if base == nil then return add > 0 and add or nil end
    return base + add
  end
  function Instance:signal(name, reads, identity_result)
    local listed, handled = self:from_sources(name, reads, identity_result)
    if handled then
      return self:plus(name, reads, listed, identity_result)
    end
    return nil
  end
  function Instance:hashes_of(relation)
    local cached = self.hashes[relation]
    if cached ~= nil then return cached end
    local seen, rows = {}, {}
    local declared = self.fields[relation]
    if type(declared) == "table" then
      for index = 1, #declared do
        if seen[declared[index]] == nil then
          seen[declared[index]] = true
          rows[#rows + 1] = declared[index]
        end
      end
    end
    for _, source in pairs(self.columns) do
      if source.relation == relation and type(source.from) == "string" and
         string.sub(source.from, 1, 2) == "0x" and seen[source.from] == nil then
        seen[source.from] = true
        rows[#rows + 1] = source.from
      end
    end
    self.hashes[relation] = rows
    return rows
  end
  local CALL_TABLES = { "equipment", "backpacks", "underbarrels" }
  local CALL_SINGLES = { "avatar", "player" }
  function Instance:call_of(type_hash)
    for index = 1, #CALL_TABLES do
      local row = (self.identity[CALL_TABLES[index]] or {})[type_hash]
      if row ~= nil and type(row.call) == "string" then return row.call end
    end
    for index = 1, #CALL_SINGLES do
      local one = self.identity[CALL_SINGLES[index]] or {}
      if one.type == type_hash and type(one.call) == "string" then
        return one.call
      end
    end
    return nil
  end
  function Instance:slots_of(type_hash)
    if type_hash == nil then return nil end
    local known = self.slots[type_hash]
    if known ~= nil then
      if known == false then return nil end
      return known
    end
    local call = self:call_of(type_hash)
    if call == nil then
      self.counters.no_call_string = self.counters.no_call_string + 1
      self.slots[type_hash] = false
      return nil
    end
    if not callable(Net, "object_info") then
      self.slots[type_hash] = false
      return nil
    end
    self.counters.declaration_calls = self.counters.declaration_calls + 1
    local ok, info = pcall(Net.object_info, call)
    if not ok or type(info) ~= "table" or type(info.fields) ~= "table" then
      self.counters.declaration_errors = self.counters.declaration_errors + 1
      self.slots[type_hash] = false
      return nil
    end
    local at, counted, shape = {}, 0, {}
    for index, descriptor in pairs(info.fields) do
      if type(index) == "number" and index == math.floor(index) and
         type(descriptor) == "table" and type(descriptor.id) == "string" then
        local hash = "0x" .. pad16(descriptor.id, 8)
        at[hash] = index
        counted = counted + 1
        shape[#shape + 1] = hash .. ":" .. tostring(descriptor.type) ..
                            ":" .. tostring(descriptor.bits) ..
                            ":" .. tostring(descriptor.min) ..
                            ":" .. tostring(descriptor.max)
      end
    end
    if counted == 0 then
      self.counters.declaration_errors = self.counters.declaration_errors + 1
      self.slots[type_hash] = false
      return nil
    end
    table.sort(shape)
    self.declared_note[type_hash] = table.concat(shape, ",")
    self.counters.declaration_types = self.counters.declaration_types + 1
    self.counters.declaration_fields = self.counters.declaration_fields +
      counted
    self.slots[type_hash] = at
    return at
  end
  local function seated(value, seat, counters)
    if value[seat] ~= nil then return value[seat] end
    for _, one in pairs(value) do
      if type(one) == "table" and one[seat] ~= nil then
        counters.nested_reads = counters.nested_reads + 1
        return one[seat]
      end
    end
  end
  function Instance:batched(session, goid, relation, type_hash)
    if not callable(GS, "game_object_field_batched") then return nil end
    local token = tostring(session)
    if self.out_generation ~= token then
      self.out_generation = token
      self.out = {}
      self.slots = {}
    else
      for key in pairs(self.out) do self.out[key] = nil end
    end
    self.counters.batched_calls = self.counters.batched_calls + 1
    local ok, value = pcall(GS.game_object_field_batched, session, goid,
                            self.out)
    if not ok then
      self.counters.batched_errors = self.counters.batched_errors + 1
      return nil
    end
    if type(value) ~= "table" then return nil end
    local at = self:slots_of(type_hash)
    if at == nil then
      return nil
    end
    self.types_seen[type_hash] = true
    local wanted, taken = self:hashes_of(relation), {}
    for index = 1, #wanted do
      local hash = wanted[index]
      local seat = at[hash]
      if seat == nil then
        self.counters.hash_not_declared = self.counters.hash_not_declared + 1
      else
        local cell_value = seated(value, seat, self.counters)
        if cell_value ~= nil then taken[hash] = cell_value end
      end
    end
    return taken
  end
  function Instance:live(session, goid)
    if not callable(GS, "game_object_exists") then return false end
    local ok, alive = pcall(GS.game_object_exists, session, goid)
    if not ok or alive ~= true then
      self.counters.exists_misses = self.counters.exists_misses + 1
      return false
    end
    return true
  end
  function Instance:read_relations(session, identity_result, window)
    local reads = {}
    for relation in pairs(self.fields) do
      local slot = RELATION_SLOT[relation]
      local named = slot ~= nil and identity_result[slot] or nil
      if named == nil or named.goid == nil then
        self.counters.relations_skipped = self.counters.relations_skipped + 1
        window.relations[relation] = "no-object"
      elseif not self:live(session, named.goid) then
        self.counters.relations_skipped = self.counters.relations_skipped + 1
        window.relations[relation] = "object-gone"
      else
        local read = self:batched(session, named.goid, relation,
                                  named.type)
        if read == nil then
          self.counters.relations_skipped = self.counters.relations_skipped + 1
          window.relations[relation] = "batched-failed"
        else
          self.counters.relations_read = self.counters.relations_read + 1
          window.relations[relation] = "read"
          reads[relation] = read
          window.bearers[relation] = named.goid
        end
      end
    end
    return reads
  end
  local function cell(read, source)
    if read == nil then return nil end
    local value = read[source.from]
    if value == nil then return nil end
    if source.slot == nil then return value end
    if type(value) ~= "number" then return nil end
    local divisor = 4 ^ source.slot
    return math.floor(value / divisor) % 4
  end
  function Instance:provide(identity_result, session)
    self.counters.windows = self.counters.windows + 1
    local window = { cells = {}, relations = {}, bearers = {} }
    identity_result = identity_result or {}
    local reads = self:read_relations(session, identity_result, window)
    for column, source in pairs(self.columns) do
      local value = nil
      if source.const ~= nil then
        value = source.const
      elseif type(source.from) == "string" and
             string.sub(source.from, 1, 7) == "signal:" then
        value = self:signal(string.sub(source.from, 8), reads,
                            identity_result)
        if value ~= nil then
          self.counters.signals_filled = self.counters.signals_filled + 1
        end
      elseif type(source.from) == "string" and
             string.sub(source.from, 1, 7) == "action:" then
        local action = identity_result.action
        if type(action) == "table" then
          value = action[string.sub(source.from, 8)]
          if value ~= nil then
            self.counters.action_filled =
              (self.counters.action_filled or 0) + 1
          end
        end
      elseif source.from ~= nil then
        value = cell(reads[source.relation], source)
      end
      if value ~= nil then
        window.cells[column] = value
        self.counters.columns_filled = self.counters.columns_filled + 1
      else
        self.counters.columns_missing = self.counters.columns_missing + 1
      end
    end
    window.reading = self:reading(reads)
    return window
  end
  function Instance:reading(reads)
    local parts = {}
    for relation, read in pairs(reads or {}) do
      local hashes = {}
      for hash in pairs(read) do hashes[#hashes + 1] = hash end
      table.sort(hashes)
      for index = 1, #hashes do
        local value = read[hashes[index]]
        if type(value) ~= "table" then
          parts[#parts + 1] = relation .. "\1" .. hashes[index] ..
                              "\1" .. tostring(value)
        end
      end
    end
    table.sort(parts)
    return table.concat(parts, "\2")
  end
  function Instance:declared_of(type_hash)
    if type_hash == nil then return nil end
    return self.declared_note[type_hash]
  end
  local POPULATIONS = { "equipment", "backpacks", "underbarrels" }
  function Instance:coverage()
    local out, known = {}, {}
    local avatar = (self.identity.avatar or {}).type
    if avatar ~= nil then
      known[avatar] = true
      out[#out + 1] = "avatar=" ..
        (self.types_seen[avatar] and "1" or "0") .. "/1"
    end
    for index = 1, #POPULATIONS do
      local name = POPULATIONS[index]
      local rows = self.identity[name]
      if type(rows) == "table" then
        local total, seen = 0, {}
        for type_hash in pairs(rows) do
          total = total + 1
          known[type_hash] = true
          if self.types_seen[type_hash] then seen[#seen + 1] = type_hash end
        end
        if total > 0 then
          table.sort(seen)
          local named = {}
          for index = 1, #seen do
            if index > 12 then break end
            named[#named + 1] = seen[index]
          end
          out[#out + 1] = name .. "=" .. #seen .. "/" .. total ..
            (#named > 0 and ("=" .. table.concat(named, ",")) or "")
        end
      end
    end
    local stray = {}
    for type_hash in pairs(self.types_seen) do
      if not known[type_hash] then stray[#stray + 1] = type_hash end
    end
    if #stray > 0 then
      table.sort(stray)
      local named = {}
      for index = 1, #stray do
        if index > 5 then break end
        named[#named + 1] = stray[index]
      end
      out[#out + 1] = "stray=" .. #stray .. "=" ..
                      table.concat(named, ",")
    end
    return table.concat(out, " ")
  end
  function Instance:report()
    local out = {}
    for key, value in pairs(self.counters) do out[key] = value end
    return out
  end
  return Core
end)()
local __contract_ok_1, __contract_export_1 = pcall(function() return Provider end)
if not __contract_ok_1 or __contract_export_1 == nil then error("40-provider: missing export Provider", 0) end
return {["Provider"]=__contract_export_1}
end
end)()
setfenv(__factory, __module_environment("40-provider", true))
local __exports = __module_run("40-provider", __factory, __imports, __module_log)
if __exports ~= nil then
__module_registry["Provider"] = __exports["Provider"]
end
end
if #__module_errors > 0 then error(table.concat(__module_errors, "; ")) end
return __module_registry
