-- Included by the scripts that change concurrency and deduplication keys, after JobStore.Common.lua.
-- A job's 'ckey' and 'dkey' fields hold its concurrency and deduplication keys, and are absent (or empty) when it has none.
local function present(value)
    return value and value ~= '' and value or false
end

-- A job waiting for its concurrency key is a member of its name's waiting set: the key (length first, so no key's
-- members are a prefix of another's), when the job became due and its id. Ordered by key, then due time.
local function keyPrefix(concurrencyKey)
    return string.format('%08x', #concurrencyKey) .. concurrencyKey
end

local function waitingMember(concurrencyKey, due, id)
    return keyPrefix(concurrencyKey) .. string.format('%020.0f', tonumber(due)) .. id
end

-- Makes the job waiting longest for the concurrency key claimable again, by moving it back to the queued set.
local function scheduleNextWaiting(waitingKey, queuedKey, concurrencyKey)
    local prefix = keyPrefix(concurrencyKey)
    local next = redis.call('ZRANGEBYLEX', waitingKey, '[' .. prefix, '(' .. prefix .. '\255', 'LIMIT', 0, 1)[1]
    if next then
        redis.call('ZREM', waitingKey, next)
        redis.call('ZADD', queuedKey, tonumber(string.sub(next, #prefix + 1, #prefix + 20)), string.sub(next, #prefix + 21))
    end
end

-- Releases the concurrency key if the job holds it, and gives the next job waiting for it its turn.
local function releaseKey(holdersKey, waitingKey, queuedKey, concurrencyKey, id)
    if concurrencyKey and redis.call('HGET', holdersKey, concurrencyKey) == id then
        redis.call('HDEL', holdersKey, concurrencyKey)
        scheduleNextWaiting(waitingKey, queuedKey, concurrencyKey)
    end
end

-- Forgets a queued job that leaves without being claimed: frees its deduplication key, and if it was waiting for its
-- concurrency key (or is the one a released key made claimable), lets the next job waiting for the key have its turn.
local function releaseQueued(holdersKey, waitingKey, queuedKey, pendingKey, concurrencyKey, deduplicationKey, available, id)
    if deduplicationKey and redis.call('HGET', pendingKey, deduplicationKey) == id then
        redis.call('HDEL', pendingKey, deduplicationKey)
    end
    if concurrencyKey then
        redis.call('ZREM', waitingKey, waitingMember(concurrencyKey, available, id))
        if redis.call('HEXISTS', holdersKey, concurrencyKey) == 0 then
            scheduleNextWaiting(waitingKey, queuedKey, concurrencyKey)
        end
    end
end
