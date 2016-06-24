using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Nito.AsyncEx;

namespace Foundatio.Tests.Extensions {
    public static class TaskExtensions {
        public static Task WaitAsync(this AsyncCountdownEvent countdownEvent, CancellationToken cancellationToken = default(CancellationToken)) {
            // return Task.WhenAny(countdownEvent.WaitAsync(), cancellationToken.AsTask());
            return countdownEvent.WaitAsync().WaitAsync(cancellationToken);
        }

        public static Task WaitAsync(this AsyncCountdownEvent countdownEvent, TimeSpan timeout) {
            return countdownEvent.WaitAsync(timeout.ToCancellationToken());
        }
    }
}