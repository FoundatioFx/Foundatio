-- Preceded by: local QUEUED, RUNNING, SUCCEEDED = '<status>', '<status>', '<status>'
-- ARGV[1] is always the key prefix, including the store's hash tag.
local prefix = ARGV[1]
local time = redis.call('TIME')
local now = tonumber(time[1]) * 1000000 + tonumber(time[2])

local function str(number)
    return string.format('%.0f', number)
end

local function jobKey(id)
    return prefix .. 'job:' .. id
end

local function queuedKey(name)
    return prefix .. 'queued:' .. name
end

local function runningKey(name)
    return prefix .. 'running:' .. name
end

local function indexKeys(status, name)
    return { prefix .. 'index', prefix .. 'index:name:' .. name, prefix .. 'index:status:' .. status, prefix .. 'index:status:' .. status .. ':' .. name }
end

local function isExpired(expiresAt)
    return expiresAt and tonumber(expiresAt) <= now
end

local function moveIndexes(member, name, from, to)
    local old = indexKeys(from, name)
    local new = indexKeys(to, name)
    redis.call('ZREM', old[3], member)
    redis.call('ZREM', old[4], member)
    redis.call('ZADD', new[3], 0, member)
    redis.call('ZADD', new[4], 0, member)
end

local function removeJob(id)
    local key = jobKey(id)
    local job = redis.call('HMGET', key, 'name', 'status', 'index')
    if job[1] then
        for _, index in ipairs(indexKeys(job[2], job[1])) do
            redis.call('ZREM', index, job[3])
        end
        redis.call('ZREM', queuedKey(job[1]), id)
        redis.call('ZREM', runningKey(job[1]), id)
    end
    redis.call('ZREM', prefix .. 'expiring', id)
    return redis.call('DEL', key)
end

local function removeExpired(limit)
    for _, id in ipairs(redis.call('ZRANGEBYSCORE', prefix .. 'expiring', '-inf', now, 'LIMIT', 0, limit)) do
        removeJob(id)
    end
end

