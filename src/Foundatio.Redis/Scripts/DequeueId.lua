local item = redis.call('RPOPLPUSH', @queueListName, @workListName);
if item then
    local dequeuedTimeKey = @listPrefix .. ':' .. item .. ':dequeued';
    local renewedTimeKey = @listPrefix .. ':' .. item .. ':renewed';
    redis.call('SET', dequeuedTimeKey, @now, 'PX', @timeout);
    redis.call('SET', renewedTimeKey, @now, 'PX', @timeout);
    return item;
else
    return nil;
end