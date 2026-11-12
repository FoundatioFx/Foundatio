-- Preceded by: local QUEUED, RUNNING, SUCCEEDED, CANCELLED = '<status>', '<status>', '<status>', '<status>'
-- Every key a script touches is passed in KEYS; scripts never build key names. The store's keys share one hash tag.
local time = redis.call('TIME')
local now = tonumber(time[1]) * 1000000 + tonumber(time[2])

local function str(number)
    return string.format('%.0f', number)
end

local function isExpired(expiresAt)
    return expiresAt and tonumber(expiresAt) <= now
end
