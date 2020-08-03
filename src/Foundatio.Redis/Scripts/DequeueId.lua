local item = redis.call('RPOPLPUSH', @queueListName, @workListName);
if item then
    local dequeuedTimeKey = 'q:' .. @queueName .. ':' .. item .. ':dequeued';
    local renewedTimeKey = 'q:' .. @queueName .. ':' .. item .. ':renewed';
    redis.call('SET', dequeuedTimeKey, @now, 'EX', @timeout);
    redis.call('SET', renewedTimeKey, @now, 'EX', @timeout);
    return item;
else
    return nil;
end