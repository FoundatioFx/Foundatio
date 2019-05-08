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
        private readonly ILogger _logger;
        private readonly Dictionary<string, Type> _typeNameOverrides;
        private readonly ConcurrentDictionary<Type, string> _typeNameCache = new ConcurrentDictionary<Type, string>();
        private readonly ConcurrentDictionary<string, Type> _typeCache = new ConcurrentDictionary<string, Type>();

        public DefaultTypeNameSerializer(ILogger logger = null, IDictionary<string, Type> typeNameOverrides = null) {
            _logger = logger ?? NullLogger.Instance;
            if (typeNameOverrides != null)
                _typeNameOverrides = new Dictionary<string, Type>(typeNameOverrides);
        }

        public Type Deserialize(string typeName) {
            return _typeCache.GetOrAdd(typeName, newTypeName => {
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

        public string Serialize(Type type) {
            return _typeNameCache.GetOrAdd(type, newType => {
                if (_typeNameOverrides != null) {
                    var reversedMap = _typeNameOverrides.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);
                    if (reversedMap.ContainsKey(newType))
                        return reversedMap[newType];
                }
                
                return String.Concat(type.FullName, ", ", type.Assembly.GetName().Name);
            });
        }
    }
}