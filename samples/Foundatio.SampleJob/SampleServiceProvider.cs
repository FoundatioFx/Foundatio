using System;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Queues;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Foundatio.SampleJob;

public class SampleServiceProvider
{
    public static IServiceProvider Create(ILoggerFactory loggerFactory)
    {
        var container = new ServiceCollection();

        if (loggerFactory != null)
        {
            container.AddSingleton<ILoggerFactory>(loggerFactory);
            container.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        }

        var muxer = ConnectionMultiplexer.Connect("localhost", o => o.LoggerFactory = loggerFactory);
        container.AddSingleton(muxer);
        container.AddSingleton<IQueue<PingRequest>>(s => new RedisQueue<PingRequest>(o => o.ConnectionMultiplexer(muxer).RetryDelay(TimeSpan.FromSeconds(1)).WorkItemTimeout(TimeSpan.FromSeconds(5)).LoggerFactory(loggerFactory)));
        container.AddSingleton<ICacheClient>(s => new RedisCacheClient(o => o.ConnectionMultiplexer(muxer).LoggerFactory(loggerFactory)));
        container.AddSingleton<IMessageBus>(s => new RedisMessageBus(o => o.Subscriber(muxer.GetSubscriber()).LoggerFactory(loggerFactory).MapMessageTypeToClassName<EchoMessage>()));
        container.AddSingleton<ILockProvider>(s => new CacheLockProvider(s.GetRequiredService<ICacheClient>(), s.GetRequiredService<IMessageBus>(), null, loggerFactory));
        container.AddTransient<PingQueueJob>();

        return container.BuildServiceProvider();
    }
}
