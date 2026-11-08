local function nowMs()
    local time = redis.call('TIME')
    return tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
end

local function decodeFields(encoded)
    local fields = {}
    local position = 1
    local length = #encoded
    while position <= length do
        local size = struct.unpack('>I4', encoded, position)
        position = position + 4
        fields[#fields + 1] = string.sub(encoded, position, position + size - 1)
        position = position + size
    end
    return fields
end

local function isError(reply)
    return type(reply) == 'table' and reply.err ~= nil
end

