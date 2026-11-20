-- KEYS: subscriptions, instances, then one deduplication key per message that has one, then per instance subscription
--       named in ARGV its stream, delayed, delayed-data, groups, held and idle keys
-- ARGV: deduplication window ms, instance key ttl ms, publish command, number of deduplication keys, number of instance
--       subscriptions, then per instance subscription its name and wake channel prefix, then per message: delay ms, has
--       deduplication key, delayed id, has group, group key, encoded fields
-- Removes the instance subscriptions that missed their heartbeats. When the live ones are the ones named, it also
-- deduplicates the messages and adds the accepted ones to every instance subscription, waking its receiver when idle;
-- otherwise it publishes nothing, so the caller can name the live ones and call again.
-- Returns 1 when it published (0 when the named instance subscriptions were not the live ones), the topic's durable
-- subscriptions, its live instance subscriptions, the stale ones it removed, and per deduplicated message 1 when accepted
local subscriptions, instances = KEYS[1], KEYS[2]
local keyTtl, publish = tonumber(ARGV[2]), ARGV[3]
local deduplicated, named = tonumber(ARGV[4]), tonumber(ARGV[5])
local now = nowMs()

local stale = redis.call('ZRANGEBYSCORE', instances, '-inf', now)
if #stale > 0 then
    redis.call('ZREMRANGEBYSCORE', instances, '-inf', now)
end

local live = redis.call('ZRANGE', instances, 0, -1)
local current = #live == named
if current then
    local names = {}
    for i = 1, named do
        names[ARGV[4 + 2 * i]] = true
    end
    for _, name in ipairs(live) do
        if not names[name] then
            current = false
            break
        end
    end
end
if not current then
    return { 0, redis.call('SMEMBERS', subscriptions), live, stale, {} }
end

local accepted = {}
for i = 1, deduplicated do
    accepted[i] = redis.call('SET', KEYS[2 + i], '1', 'NX', 'PX', tonumber(ARGV[1])) and 1 or 0
end

local firstMessage = 6 + 2 * named
for j = 1, firstMessage <= #ARGV and named or 0 do
    local base = 2 + deduplicated + (j - 1) * 6
    local k = { stream = KEYS[base + 1], delayed = KEYS[base + 2], delayedData = KEYS[base + 3], groups = KEYS[base + 4], held = KEYS[base + 5] }
    -- A missing stream belongs to a subscription whose keys were lost; its heartbeat restores it.
    if redis.call('EXISTS', k.stream) == 1 then
        local available = 0
        local created = false
        local d = 0
        for i = firstMessage, #ARGV, 6 do
            local copy = true
            if ARGV[i + 1] == '1' then
                d = d + 1
                copy = accepted[d] == 1
            end
            if copy then
                local delay = tonumber(ARGV[i])
                local group = nil
                if ARGV[i + 3] == '1' then
                    group = ARGV[i + 4]
                end
                if send(k, now, delay > 0 and now + delay or 0, ARGV[i + 2], ARGV[i + 5], group) then
                    available = available + 1
                end
                created = created or group ~= nil or delay > 0
            end
        end
        -- The stream expires with the subscription; so must the keys a grouped or delayed message may have created.
        if created then
            for key = base + 2, base + 5 do
                redis.call('PEXPIRE', KEYS[key], keyTtl)
            end
        end
        wake(KEYS[base + 6], available, ARGV[5 + 2 * j], publish)
    end
end

return { 1, redis.call('SMEMBERS', subscriptions), live, stale, accepted }
