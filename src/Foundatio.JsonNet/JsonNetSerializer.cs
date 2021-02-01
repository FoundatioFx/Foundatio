﻿using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Foundatio.Serializer {
    public class JsonNetSerializer : ITextSerializer {
        private readonly JsonSerializer _serializer;

        public JsonNetSerializer(JsonSerializerSettings settings = null) {
            _serializer = JsonSerializer.Create(settings ?? new JsonSerializerSettings());
        }

        public void Serialize(object data, Stream outputStream) {
            var writer = new JsonTextWriter(new StreamWriter(outputStream));
            _serializer.Serialize(writer, data, data.GetType());
            writer.Flush();
        }

        public object Deserialize(Stream inputStream, Type objectType) {
            using (var sr = new StreamReader(inputStream))
            using (var reader = new JsonTextReader(sr))
                return _serializer.Deserialize(reader, objectType);
        }

        public Task SerializeAsync(object data, Stream outputStream, CancellationToken cancellationToken) {
            Serialize(data, outputStream);
            return Task.CompletedTask;
        }

        public ValueTask<object> DeserializeAsync(Stream inputStream, Type objectType, CancellationToken cancellationToken) {
            return new ValueTask<object>(Deserialize(inputStream, objectType));
        }
    }
}
