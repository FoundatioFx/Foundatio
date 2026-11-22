using System;
using Foundatio.Messaging.Benchmarks.Targets;

namespace Foundatio.Messaging.Benchmarks;

public static class TargetFactory
{
    public static IBenchTarget Create(BenchOptions options, BrokerCalls calls) => (options.Transport, options.Api) switch
    {
        (_, Api.V2) => new V2Target(options, calls),
        ("redis", Api.Legacy) => RedisTargets.Legacy(options, (RedisCalls)calls),
        ("redis", Api.Raw) => new RawRedisTarget(options, (RedisCalls)calls),
        ("rabbitmq", Api.Legacy) => RabbitMQTargets.Legacy(options),
        ("rabbitmq", Api.Raw) => new RawRabbitMQTarget(options),
        ("kafka", Api.Legacy) => KafkaTargets.Legacy(options),
        ("kafka", Api.Raw) => new RawKafkaTarget(options),
        ("sqs", Api.Legacy) => SqsTargets.Legacy(options),
        ("sqs", Api.Raw) => new RawSqsTarget(options),
        ("servicebus", Api.Legacy) => ServiceBusTargets.Legacy(options),
        ("servicebus", Api.Raw) => new RawServiceBusTarget(options),
        ("azurestorage", Api.Legacy) => AzureStorageTargets.Legacy(options),
        ("azurestorage", Api.Raw) => new RawAzureStorageTarget(options),
        _ => throw new ArgumentException($"No {options.Api} target for {options.Transport}")
    };
}
