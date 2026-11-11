using System;
using System.Threading.Tasks;
using Confluent.Kafka;
using Foundatio.Messaging;
using Microsoft.Extensions.Logging;

namespace Foundatio.Kafka.Subscribe;

public class Program
{
    public static async Task Main()
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace).AddConsole();
        });
        var logger = loggerFactory.CreateLogger<Program>();

        logger.LogInformation("Waiting to receive messages, press enter to quit...");

        using var messageBus = new KafkaMessageBus(new KafkaMessageBusOptions
        {
            BootstrapServers = "localhost:9092",
            Topic = "sample-topic",
            GroupId = "test-group-1",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            //EnableAutoCommit = false,
            //EnableAutoOffsetStore = false,
            AllowAutoCreateTopics = true,
            //Debug = "consumer,cgrp,topic,fetch",
            LoggerFactory = loggerFactory
        });

        await messageBus.SubscribeAsync<MyMessage>(msg =>
        {
            logger.LogInformation($"Got subscriber {messageBus.MessageBusId} message: {msg.Hey}");
        });

        Console.ReadLine();
    }
}
