using System;

namespace Foundatio.Redis.Metrics {
    public struct MetricKey : IEquatable<MetricKey> {
        public MetricKey(long minute, string name) {
            Minute = minute;
            Name = name;
        }

        public long Minute { get; }
        public string Name { get; }

        public bool Equals(MetricKey other) {
            return Minute == other.Minute && String.Equals(Name, other.Name);
        }

        public override bool Equals(object obj) {
            if (ReferenceEquals(null, obj))
                return false;

            return obj is MetricKey && Equals((MetricKey)obj);
        }

        public override int GetHashCode() {
            unchecked {
                return (Minute.GetHashCode() * 397) ^ (Name?.GetHashCode() ?? 0);
            }
        }

        public static bool operator ==(MetricKey left, MetricKey right) {
            return left.Equals(right);
        }

        public static bool operator !=(MetricKey left, MetricKey right) {
            return !left.Equals(right);
        }
    }
}