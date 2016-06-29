using System;
using System.Threading.Tasks;
using Nito.AsyncEx;

namespace Foundatio.Tests.Extensions {
    public static class TaskExtensions {
        public static Task WaitAsync(this AsyncCountdownEvent countdownEvent, TimeSpan timeout) {
            return Task.WhenAny(countdownEvent.WaitAsync(), Task.Delay(timeout));
        }
    }
}