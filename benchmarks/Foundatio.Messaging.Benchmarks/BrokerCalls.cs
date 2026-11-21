using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using StackExchange.Redis;
using StackExchange.Redis.Profiling;

namespace Foundatio.Messaging.Benchmarks;

/// <summary>Counts the calls the client process makes to the broker, by operation.</summary>
public abstract class BrokerCalls : IAsyncDisposable
{
    public virtual bool IsSupported => true;

    public abstract void Reset();

    public abstract Dictionary<string, long> Snapshot();

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    public static BrokerCalls Create(BenchOptions options) => options.Transport switch
    {
        "redis" => new RedisCalls(options),
        "sqs" => new HttpCalls(),
        "servicebus" => new ServiceBusCalls(),
        "rabbitmq" when options.RabbitManagement is not null => new RabbitMQCalls(options.RabbitManagement),
        _ => new NoCalls()
    };
}

public sealed class NoCalls : BrokerCalls
{
    public override bool IsSupported => false;
    public override void Reset() { }
    public override Dictionary<string, long> Snapshot() => [];
}

/// <summary>Every command on one shared multiplexer, from the StackExchange.Redis profiler.</summary>
public sealed class RedisCalls : BrokerCalls
{
    private ProfilingSession _session = new();

    public RedisCalls(BenchOptions options)
    {
        Connection = ConnectionMultiplexer.Connect(options.Redis);
        Connection.RegisterProfiler(() => _session);
    }

    public ConnectionMultiplexer Connection { get; }

    public override void Reset()
    {
        var old = _session;
        _session = new ProfilingSession();
        old.FinishProfiling();
    }

    public override Dictionary<string, long> Snapshot()
    {
        var old = _session;
        _session = new ProfilingSession();
        return old.FinishProfiling().GroupBy(c => c.Command).ToDictionary(g => g.Key, g => (long)g.Count());
    }

    public override async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync();
        await base.DisposeAsync();
    }
}

/// <summary>Every HTTP request, keyed by its AWS action, from the HttpClient diagnostic source.</summary>
public sealed class HttpCalls : BrokerCalls, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private readonly ConcurrentDictionary<string, long> _counts = new();
    private readonly List<IDisposable> _subscriptions = [];

    public HttpCalls()
    {
        _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
    }

    public override void Reset() => _counts.Clear();

    public override Dictionary<string, long> Snapshot() => _counts.ToDictionary(p => p.Key, p => p.Value);

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "HttpHandlerDiagnosticListener")
            lock (_subscriptions)
                _subscriptions.Add(listener.Subscribe(this));
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key != "System.Net.Http.HttpRequestOut.Start" || value.Value is null)
            return;

        var request = value.Value.GetType().GetProperty("Request")?.GetValue(value.Value) as HttpRequestMessage;
        string action = "http";
        if (request is not null)
        {
            if (request.Headers.TryGetValues("X-Amz-Target", out var targets))
                action = targets.First();
            else if (request.Content?.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded")
                action = "query:" + request.RequestUri?.Host;
        }

        _counts.AddOrUpdate(action, 1, (_, c) => c + 1);
    }

    public void OnCompleted() { }
    public void OnError(Exception error) { }

    public override ValueTask DisposeAsync()
    {
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        return base.DisposeAsync();
    }
}

/// <summary>Every operation the Service Bus SDK starts, from its EventSource.</summary>
public sealed class ServiceBusCalls : BrokerCalls
{
    private readonly Listener _listener = new();

    public override void Reset() => _listener.Counts.Clear();

    public override Dictionary<string, long> Snapshot() => _listener.Counts.ToDictionary(p => p.Key, p => p.Value);

    public override ValueTask DisposeAsync()
    {
        _listener.Dispose();
        return base.DisposeAsync();
    }

    private sealed class Listener : EventListener
    {
        public ConcurrentDictionary<string, long> Counts { get; } = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Azure-Messaging-ServiceBus")
                EnableEvents(eventSource, EventLevel.Verbose);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            string? name = eventData.EventName;
            if (name is null)
                return;
            // one count per broker operation: the SDK logs each operation's start
            if (name.EndsWith("Start", StringComparison.Ordinal) || name.EndsWith("StartCore", StringComparison.Ordinal))
                Counts.AddOrUpdate(name, 1, (_, c) => c + 1);
            else if (name.Contains("Exception", StringComparison.Ordinal) && eventData.Payload?.Any(p => p?.ToString()?.Contains("throttled", StringComparison.OrdinalIgnoreCase) == true) == true)
                Counts.AddOrUpdate("throttled", 1, (_, c) => c + 1);
        }
    }
}

/// <summary>Broker-wide AMQP operation counts from the management API's <c>/api/overview</c>.</summary>
public sealed class RabbitMQCalls : BrokerCalls
{
    private static readonly string[] Stats = ["publish", "confirm", "deliver", "deliver_no_ack", "get", "get_no_ack", "get_empty", "ack", "redeliver", "return_unroutable", "drop_unroutable"];
    private readonly HttpClient _client;
    private Dictionary<string, long> _baseline = [];

    public RabbitMQCalls(string management)
    {
        var uri = new Uri(management);
        _client = new HttpClient { BaseAddress = new Uri(uri.GetLeftPart(UriPartial.Authority)) };
        string credentials = String.IsNullOrEmpty(uri.UserInfo) ? "guest:guest" : Uri.UnescapeDataString(uri.UserInfo);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
    }

    public override void Reset() => _baseline = Read();

    public override Dictionary<string, long> Snapshot()
    {
        // the management API's counters lag by up to its collection interval
        Task.Delay(TimeSpan.FromSeconds(6)).GetAwaiter().GetResult();
        var now = Read();
        return now.ToDictionary(p => p.Key, p => p.Value - _baseline.GetValueOrDefault(p.Key)).Where(p => p.Value != 0).ToDictionary();
    }

    private Dictionary<string, long> Read()
    {
        string json = _client.GetStringAsync("/api/overview").GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(json);
        var counts = new Dictionary<string, long>();
        if (document.RootElement.TryGetProperty("message_stats", out var stats))
            foreach (string name in Stats)
                if (stats.TryGetProperty(name, out var value))
                    counts[name] = value.GetInt64();

        return counts;
    }

    public override ValueTask DisposeAsync()
    {
        _client.Dispose();
        return base.DisposeAsync();
    }
}
