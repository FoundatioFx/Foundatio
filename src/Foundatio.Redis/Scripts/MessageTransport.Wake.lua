-- KEYS: idle
-- ARGV: wake channel prefix, publish command
-- Passes on a wake-up that reached a receiver which is not receiving: wakes the idle receiver that was busy most recently.
-- Returns the number woken
return wake(KEYS[1], 1, ARGV[1], ARGV[2])
