local Policy = {}
Policy.__index = Policy

local function finite(value)
    return type(value) == "number" and value == value and
        value >= 0 and value < math.huge
end

function Policy.new()
    return setmetatable({ fire = false, last_sent = -math.huge }, Policy)
end

function Policy:reset()
    self.weapon, self.ammo, self.seen_at, self.pending = nil, nil, nil, nil
    self.fire_wait_until = nil
    self.fire = false
end

function Policy:step(sample, now)
    if not sample or sample.active ~= true then
        self:reset()
        return nil
    end
    local firing = sample.fire == true
    local fire_edge = firing and not self.fire
    self.fire = firing
    local weapon = sample.weapon
    if weapon == nil or not finite(sample.ammo) then
        -- Keep a short swap transition, but never use stale ammunition to reload.
        if self.seen_at and now - self.seen_at > 0.5 then
            self.weapon, self.ammo, self.pending = nil, nil, nil
        end
        if fire_edge then self.fire_wait_until = now + 0.25 end
        return nil
    end
    local swapped = self.weapon ~= nil and self.weapon ~= weapon
    local exhausted = self.weapon == weapon and finite(self.ammo) and
        self.ammo > 0 and sample.ammo == 0
    self.weapon, self.ammo, self.seen_at = weapon, sample.ammo, now
    if sample.ammo > 0 then
        self.pending, self.fire_wait_until = nil, nil
        return nil
    end
    if self.pending and (self.pending.weapon ~= weapon or
        now > self.pending.until_time) then self.pending = nil end
    local reason = exhausted and "ammo-exhausted" or
        swapped and "weapon-swapped" or
        (fire_edge or (self.fire_wait_until and now <= self.fire_wait_until))
            and "fire-attempt" or nil
    self.fire_wait_until = nil
    if reason then
        self.pending = { weapon = weapon, reason = reason, until_time = now + 0.35 }
    end
    if not self.pending then return nil end
    if sample.reloading == true then
        self.pending = nil
        return nil
    end
    if sample.reloading ~= false or not finite(sample.reserve) or
        sample.reserve <= 0 or sample.manual_reload or
        now - self.last_sent < 0.35 then return nil end
    return self.pending.reason
end

function Policy:sent(now)
    self.last_sent, self.pending = now, nil
end

return Policy
