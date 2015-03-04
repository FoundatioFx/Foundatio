using System;

namespace Foundatio.Serializer {
    public interface ISerializer {
        T Deserialize<T>(string value);
        object Deserialize(string value, Type objectType);
        string Serialize(object value);
    }
}
