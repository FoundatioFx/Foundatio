using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Messaging {
    public interface ITypeNameSerializer {
        string Serialize(Type type);
        Type Deserialize(string typeName);
    }

    public class DefaultTypeNameSerializer : ITypeNameSerializer {
        private readonly Dictionary<string, Type> _typeNameOverrides;
        private readonly ILogger _logger;

        public DefaultTypeNameSerializer(ILogger logger = null, IDictionary<string, Type> typeNameOverrides = null) {
            _logger = logger ?? NullLogger.Instance;
            _typeNameOverrides = typeNameOverrides != null ? new Dictionary<string, Type>(typeNameOverrides) : new Dictionary<string, Type>();
        }

        private readonly ConcurrentDictionary<string, Type> _knownMessageTypesCache = new ConcurrentDictionary<string, Type>();
        public Type Deserialize(string typeName) {
            return _knownMessageTypesCache.GetOrAdd(typeName, newTypeName => {
                if (_typeNameOverrides != null && _typeNameOverrides.ContainsKey(newTypeName))
                    return _typeNameOverrides[newTypeName];

                try {
                    return Type.GetType(newTypeName);
                } catch (Exception ex) {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(ex, "Error getting message type: {MessageType}", newTypeName);

                    return null;
                }
            });
        }

        private readonly ConcurrentDictionary<Type, string> _mappedMessageTypesCache = new ConcurrentDictionary<Type, string>();
        public string Serialize(Type type) {
            return _mappedMessageTypesCache.GetOrAdd(type, newType => {
                var reversedMap = _typeNameOverrides.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);
                if (reversedMap.ContainsKey(newType))
                    return reversedMap[newType];
                
                return String.Concat(type.FullName, ", ", type.Assembly.GetName().Name);
            });
        }
    }
}