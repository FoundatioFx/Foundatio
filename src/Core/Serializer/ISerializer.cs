namespace Foundatio.Serializer {
    public interface ISerializer {
        T Deserialize<T>(byte[] value);
        byte[] Serialize(object value);
    }
}
