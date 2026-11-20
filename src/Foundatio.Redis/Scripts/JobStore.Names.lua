-- KEYS: jobs
-- Returns each job's name, or '' for a job that does not exist. Changes nothing.
local names = {}
for i = 1, #KEYS do
    names[i] = redis.call('HGET', KEYS[i], 'name') or ''
end
return names
