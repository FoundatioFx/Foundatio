﻿using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging {
    public interface IMessageSubscriber {
        Task SubscribeAsync<T>(Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default, Func<T, bool> messagefilter = null) where T : class;
        
    }

    public static class MessageBusExtensions {
        public static Task SubscribeAsync<T>(this IMessageSubscriber subscriber, Func<T, Task> handler, CancellationToken cancellationToken = default, Func<T, bool> messagefilter = null) where T : class {
            return subscriber.SubscribeAsync<T>((msg, token) => handler(msg), cancellationToken, messagefilter);
        }

        public static Task SubscribeAsync<T>(this IMessageSubscriber subscriber, Action<T> handler, CancellationToken cancellationToken = default, Func<T, bool> messagefilter = null) where T : class {
            return subscriber.SubscribeAsync<T>((msg, token) => {
                handler(msg);
                return Task.CompletedTask;
            }, cancellationToken, messagefilter);
        }


      
    }


}
