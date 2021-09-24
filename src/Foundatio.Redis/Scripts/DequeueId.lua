local item = redis.call('RPOPLPUSH', @queueListName, @workListName);
if item then
    local dequeuedTimeKey = 'q:' .. @queueName .. ':' .. item .. ':dequeued';
    local renewedTimeKey = 'q:' .. @queueName .. ':' .. item .. ':renewed';
    redis.call('SET', dequeuedTimeKey, @now, 'PX', @timeout);
    redis.call('SET', renewedTimeKey, @now, 'PX', @timeout);
    return item;
else
    return nil;
end