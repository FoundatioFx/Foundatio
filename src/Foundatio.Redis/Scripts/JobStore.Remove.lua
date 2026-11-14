-- KEYS: job, expiring, then (when the job was found) queued, running, index, name index, status index, status name index
-- ARGV: id, name, status, index member, '1' to remove the job only when it has expired
-- Returns 1 when a live job was removed, 2 when an expired one was, 0 when there was nothing to remove, and -1 when
-- the job's name, status or index member is not the one given (it changed since the caller read it).
local id = ARGV[1]
local job = redis.call('HMGET', KEYS[1], 'name', 'status', 'index', 'expires')
if not job[1] then
    redis.call('ZREM', KEYS[2], id)
    return 0
end
if #KEYS < 8 or job[1] ~= ARGV[2] or job[2] ~= ARGV[3] or job[3] ~= ARGV[4] then
    return -1
end

local expired = isExpired(job[4])
if ARGV[5] == '1' and not expired then
    return 0
end

for i = 5, 8 do
    redis.call('ZREM', KEYS[i], job[3])
end
redis.call('ZREM', KEYS[3], id)
redis.call('ZREM', KEYS[4], id)
redis.call('ZREM', KEYS[2], id)
redis.call('DEL', KEYS[1])
return expired and 2 or 1
