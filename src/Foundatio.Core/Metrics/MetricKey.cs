using System;

namespace Foundatio.Metrics {
    public struct MetricKey : IEquatable<MetricKey> {
        public MetricKey(DateTime time, string name) {
            Time = time;
            Name = name;
        }

        public DateTime Time { get; }
        public string Name { get; }

        public bool Equals(MetricKey other) {
            return Time == other.Time && String.Equals(Name, other.Name);
        }

        public override bool Equals(object obj) {
            if (ReferenceEquals(null, obj))
                return false;

            return obj is MetricKey && Equals((MetricKey)obj);
        }

        public override int GetHashCode() {
            unchecked {
                return (Time.GetHashCode() * 397) ^ (Name?.GetHashCode() ?? 0);
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