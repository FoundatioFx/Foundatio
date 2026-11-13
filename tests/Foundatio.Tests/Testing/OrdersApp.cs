using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Tests.Testing;

/// <summary>A small app, registered the way its Program.cs would, used to test <c>FoundatioTestHarness</c> as an app developer would.</summary>
public static class OrdersApp
{
    /// <summary>The app's registrations, including production transport and store factories that tests must never reach.</summary>
    public static IServiceCollection AddOrdersApp(this IServiceCollection services)
    {
        services.AddSingleton<EmailSender>();
        services.AddSingleton<PriceCache>();
        services.AddFoundatioMessaging(m => m
            .UseTransport(_ => throw new InvalidOperationException("Tests must not connect to the production broker."))
            .UseServiceName("orders")
            .AddSubscriber<OrderPlacedHandler>()
            .AddConsumer<SendReceiptHandler>()
            .AddSubscriber<PriceCacheInvalidator>(new SubscriptionOptions { Scope = SubscriptionScope.Instance }));

        services.AddFoundatioJobs(j => j
            .UseStore(_ => throw new InvalidOperationException("Tests must not connect to the production job store."))
            .AddJob<GenerateInvoiceJob>()
            .AddCronJob<NightlyCleanupJob>("* * * * * *"));

        return services;
    }

    [Message("orders.placed", Topic = "orders")]
    public record OrderPlaced(int OrderId, string Email);

    [Message("orders.cancelled", Topic = "orders")]
    public record OrderCancelled(int OrderId);

    [Message("orders.send-receipt")]
    public record SendReceipt(int OrderId, string Email);

    /// <summary>A command for another service: nothing in this app consumes it.</summary>
    [Message("shipping.ship-order")]
    public record ShipOrder(int OrderId);

    [Message("catalog.price-changed")]
    public record PriceChanged(string Sku);

    [Job(Name = "orders.generate-invoice")]
    public record GenerateInvoice(int OrderId, bool Fail = false);

    [Job(Name = "orders.nightly-cleanup")]
    public sealed class NightlyCleanupJob : IJobHandler
    {
        public Task HandleAsync(IJobContext context, CancellationToken cancellationToken) => context.ReportProgressAsync(100, "cleaned", cancellationToken);
    }

    public sealed class EmailSender
    {
        public ConcurrentQueue<string> Sent { get; } = new();

        public Task SendAsync(string email, string subject)
        {
            if (email.StartsWith("bounce", StringComparison.Ordinal))
                throw new InvalidOperationException($"Mailbox {email} does not exist");

            Sent.Enqueue($"{email}: {subject}");
            return Task.CompletedTask;
        }
    }

    public sealed class OrderPlacedHandler(IMessageClient messages, IJobClient jobs) : IMessageHandler<OrderPlaced>
    {
        public async Task HandleAsync(OrderPlaced order, IMessageContext context, CancellationToken cancellationToken)
        {
            await messages.SendAsync(new SendReceipt(order.OrderId, order.Email), cancellationToken: cancellationToken);
            await messages.SendAsync(new ShipOrder(order.OrderId), cancellationToken: cancellationToken);
            await jobs.EnqueueAsync(new GenerateInvoice(order.OrderId), cancellationToken: cancellationToken);
        }
    }

    public sealed class SendReceiptHandler(EmailSender email) : IMessageHandler<SendReceipt>
    {
        public Task HandleAsync(SendReceipt receipt, IMessageContext context, CancellationToken cancellationToken)
            => email.SendAsync(receipt.Email, $"Receipt for order {receipt.OrderId}");
    }

    public sealed class PriceCache
    {
        public ConcurrentQueue<string> Invalidated { get; } = new();
    }

    public sealed class PriceCacheInvalidator(PriceCache cache) : IMessageHandler<PriceChanged>
    {
        public Task HandleAsync(PriceChanged message, IMessageContext context, CancellationToken cancellationToken)
        {
            cache.Invalidated.Enqueue(message.Sku);
            return Task.CompletedTask;
        }
    }

    public sealed class GenerateInvoiceJob : IJobHandler<GenerateInvoice>
    {
        public Task HandleAsync(GenerateInvoice arguments, IJobContext context, CancellationToken cancellationToken)
        {
            if (arguments.Fail)
                throw new InvalidOperationException($"No billing address for order {arguments.OrderId}");

            return Task.CompletedTask;
        }
    }
}
