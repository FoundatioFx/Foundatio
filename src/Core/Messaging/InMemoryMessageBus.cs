﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Utility;

namespace Foundatio.Messaging {
    public class InMemoryMessageBus : MessageBusBase, IMessageBus {
        public override Task PublishAsync(Type messageType, object message, TimeSpan? delay = null, CancellationToken cancellationToken = default(CancellationToken)) {
            if (delay.HasValue && delay.Value > TimeSpan.Zero)
                return AddDelayedMessageAsync(messageType, message, delay.Value);

            Task.Run(async () => await SendMessageToSubscribersAsync(messageType, message.Copy()).AnyContext());
            return TaskHelper.Completed();
        }
    }
}