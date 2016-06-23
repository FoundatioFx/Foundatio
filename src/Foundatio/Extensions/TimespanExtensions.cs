using System;
using System.Threading;

namespace Foundatio.Extensions {
    public static class TimespanExtensions {
        public static CancellationToken ToCancellationToken(this TimeSpan timeout) {
            if (timeout == TimeSpan.Zero)
                return new CancellationToken(true);

            if (timeout.Ticks > 0)
                return new CancellationTokenSource(timeout).Token;

            return default(CancellationToken);
        }

        public static CancellationToken ToCancellationToken(this TimeSpan? timeout, TimeSpan defaultTimeout) {
            return (timeout ?? defaultTimeout).ToCancellationToken();
        }
    }
}