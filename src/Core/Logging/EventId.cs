using System;

namespace Foundatio.Logging {
    public struct EventId {
        public EventId(int id, string name = null) {
            Id = id;
            Name = name;
        }

        public int Id { get; }

        public string Name { get; }

        public static implicit operator EventId(int i) {
            return new EventId(i);
        }
    }
}
