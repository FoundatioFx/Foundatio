-- KEYS: stream, delayed, delayed-data, groups, held, idle, then one deduplication key per message that has one
-- ARGV: deduplication window ms, create ('1' creates a missing source, '0' skips it), consumer group, wake channel prefix,
--       publish command, count ('1' returns the number of messages in the source), then per message: delay ms, has
--       deduplication key, delayed id, has group, group key, encoded fields
-- Wakes an idle receiver for each message added that is not held behind its group: a delayed one, so it learns when
-- the message is due. Returns the number of messages added (-1 when the source does not exist and was not created), the
-- number of groups released because their outstanding message was lost, and the number of messages in the source (0
-- when not counted)
local k = { stream = KEYS[1], delayed = KEYS[2], delayedData = KEYS[3], groups = KEYS[4], held = KEYS[5] }
local window = tonumber(ARGV[1])
local now = nowMs()

local released = 0
if ARGV[2] == '1' then
    released = ensureStream(k, ARGV[3], now)
elseif redis.call('EXISTS', k.stream) == 0 then
    return { -1, 0, 0 }
end

local nextKey = 7
local added = 0
local available = released

for i = 7, #ARGV, 6 do
    local accepted = true
    if ARGV[i + 1] == '1' then
        accepted = redis.call('SET', KEYS[nextKey], '1', 'NX', 'PX', window)
        nextKey = nextKey + 1
    end

    if accepted then
        local delay = tonumber(ARGV[i])
        local group = nil
        if ARGV[i + 3] == '1' then
            group = ARGV[i + 4]
        end
        if send(k, now, delay > 0 and now + delay or 0, ARGV[i + 2], ARGV[i + 5], group) then
            available = available + 1
        end
        added = added + 1
    end
end

wake(KEYS[6], available, ARGV[4], ARGV[5])

if ARGV[6] ~= '1' then
    return { added, released, 0 }
end
return { added, released, redis.call('XLEN', k.stream) + redis.call('ZCARD', k.delayed) + redis.call('HLEN', k.held) }
