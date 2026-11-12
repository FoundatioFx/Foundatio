-- KEYS: stream, delayed, delayed-data, groups, held
-- ARGV: consumer group
-- Returns the number of groups released because their outstanding message was lost
local k = { stream = KEYS[1], delayed = KEYS[2], delayedData = KEYS[3], groups = KEYS[4], held = KEYS[5] }
local released = ensureStream(k, ARGV[1], nowMs())

local created = redis.pcall('XGROUP', 'CREATE', k.stream, ARGV[1], '0')
if isError(created) and not string.find(created.err, 'BUSYGROUP', 1, true) then
    return redis.error_reply(created.err)
end

return released
