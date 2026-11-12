-- KEYS: index
-- ARGV: continuation member (empty for the first page), limit
-- Returns up to limit + 1 index members, newest first; the store reads the jobs and filters out expired ones.
local start = ARGV[1] == '' and '+' or '(' .. ARGV[1]
return redis.call('ZREVRANGEBYLEX', KEYS[1], start, '-', 'LIMIT', 0, tonumber(ARGV[2]) + 1)
