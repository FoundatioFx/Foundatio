using System;

namespace Foundatio.Metrics {
    public struct MetricKey : IEquatable<MetricKey> {
        public MetricKey(DateTime startTimeUtc, TimeSpan duration, string name) {
            StartTimeUtc = startTimeUtc;
            Duration = duration;
            Name = name;
        }

        public DateTime StartTimeUtc { get; }
        public TimeSpan Duration { get; }
        public string Name { get; }

        public DateTime EndTimeUtc => StartTimeUtc.Add(Duration);

        public bool Equals(MetricKey other) {
            return StartTimeUtc == other.StartTimeUtc && Duration == other.Duration && String.Equals(Name, other.Name);
        }

        public override bool Equals(object obj) {
            if (obj is null)
                return false;

            return obj is MetricKey key && Equals(key);
        }

        public override int GetHashCode() {
            unchecked {
                return (StartTimeUtc.GetHashCode() * 397) ^ (Duration.GetHashCode() * 397) ^ (Name?.GetHashCode() ?? 0);
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