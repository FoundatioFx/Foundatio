using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Reads a source's counts: ready messages from passive declares on a channel of its own (a declare of a queue that
/// does not exist closes the channel it was sent on), or, with a management endpoint, ready and unacknowledged
/// messages from the management API.
/// </summary>
internal sealed class RabbitMQSourceStatsReader : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly RabbitMQChannelSlot _channel;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly HttpClient? _http;
    private readonly string _virtualHost = "/";

    public RabbitMQSourceStatsReader(RabbitMQMessageTransport transport)
    {
        _channel = new RabbitMQChannelSlot(async ct => await (await transport.GetConnectionAsync(consumer: false, ct).AnyContext()).CreateChannelAsync(cancellationToken: ct).AnyContext());

        var options = transport.Options;
        Uri? connectionUri = Uri.TryCreate(options.ConnectionString, UriKind.Absolute, out var parsed) ? parsed : null;
        if (connectionUri is not null)
            _virtualHost = new ConnectionFactory { Uri = connectionUri }.VirtualHost;

        if (options.ManagementEndpoint is not { } endpoint)
            return;

        string userInfo = endpoint.UserInfo.Length > 0 ? endpoint.UserInfo : connectionUri?.UserInfo ?? String.Empty;
        _http = new HttpClient { BaseAddress = new Uri(new UriBuilder(endpoint) { UserName = String.Empty, Password = String.Empty }.Uri.AbsoluteUri.TrimEnd('/') + "/"), Timeout = RequestTimeout };
        if (userInfo.Length > 0)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(userInfo))));
    }

    public async Task<MessageSourceStats?> GetAsync(MessageSource source, string queue, string deadLetterQueue, CancellationToken cancellationToken)
    {
        if (_http is not null)
        {
            var counts = await Task.WhenAll(GetCountsAsync(queue, cancellationToken), GetCountsAsync(deadLetterQueue, cancellationToken)).AnyContext();
            return new MessageSourceStats
            {
                Source = source,
                Queued = counts[0].Ready,
                InFlight = counts[0].Unacknowledged,
                DeadLettered = counts[1].Ready + counts[1].Unacknowledged
            };
        }

        await _lock.WaitAsync(cancellationToken).AnyContext();
        try
        {
            return new MessageSourceStats
            {
                Source = source,
                Queued = await CountReadyAsync(queue, cancellationToken).AnyContext(),
                DeadLettered = await CountReadyAsync(deadLetterQueue, cancellationToken).AnyContext()
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync().AnyContext();
        _http?.Dispose();
    }

    private async Task<long> CountReadyAsync(string queue, CancellationToken cancellationToken)
    {
        try
        {
            var channel = await _channel.GetAsync(cancellationToken).AnyContext();
            return (await channel.QueueDeclarePassiveAsync(queue, cancellationToken).AnyContext()).MessageCount;
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 404)
        {
            return 0;
        }
        catch (Exception ex) when (ex is OperationInterruptedException or AlreadyClosedException)
        {
            throw new MessageBusException($"Could not count the messages in {queue}: {ex.Message}", ex);
        }
    }

    private async Task<(long Ready, long Unacknowledged)> GetCountsAsync(string queue, CancellationToken cancellationToken)
    {
        string path = $"api/queues/{Uri.EscapeDataString(_virtualHost)}/{Uri.EscapeDataString(queue)}";
        try
        {
            using var response = await _http!.GetAsync(path, cancellationToken).AnyContext();
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (0, 0);

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).AnyContext();
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).AnyContext();
            // A queue that was just declared has no statistics until the broker's next collection.
            return (GetCount(document.RootElement, "messages_ready"), GetCount(document.RootElement, "messages_unacknowledged"));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new MessageBusException($"Could not read the counts of {queue} from the RabbitMQ management API: {ex.Message}", ex);
        }
    }

    private static long GetCount(JsonElement queue, string property)
    {
        return queue.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long count) ? count : 0;
    }
}
