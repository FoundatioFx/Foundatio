using System;
using System.Linq;
using Foundatio.Jobs.Store;
using Foundatio.Messaging.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Testing;

/// <summary>
/// Added by <see cref="TestHarnessServiceCollectionExtensions.AddFoundatioTestHarness"/>. While it is registered the
/// harness owns the transport and job store: <c>UseTransport</c> and <c>UseStore</c> calls, including the providers'
/// <c>UseRedis()</c> and the like, are ignored whatever order they come in, and the runtimes use the harness's.
/// </summary>
internal sealed class TestHarnessRegistration
{
    private readonly object _lock = new();
    private InMemoryMessageTransport? _transport;
    private InMemoryJobStore? _store;

    public TestHarnessRegistration(FoundatioTestHarnessOptions options)
    {
        Options = options;
    }

    public FoundatioTestHarnessOptions Options { get; }

    /// <summary>The clock messaging, jobs, the transport and the store run on under the harness.</summary>
    public TimeProvider TimeProvider => Options.TimeProvider ?? TimeProvider.System;

    /// <summary>The harness's transport, created on first use.</summary>
    public InMemoryMessageTransport Transport
    {
        get
        {
            lock (_lock)
            {
                return _transport ??= Options.Transport ?? (Options.TransportCapabilities is { } capabilities
                    ? new InMemoryMessageTransport(capabilities, TimeProvider)
                    : new InMemoryMessageTransport(TimeProvider));
            }
        }
    }

    /// <summary>The harness's job store, created on first use.</summary>
    public InMemoryJobStore Store
    {
        get
        {
            lock (_lock)
                return _store ??= Options.JobStore ?? new InMemoryJobStore(TimeProvider);
        }
    }

    /// <summary>Whether the transport or job store is shared with other hosts.</summary>
    public bool IsShared => Options.Transport is not null || Options.JobStore is not null;

    /// <summary>Whether <paramref name="services"/> has the test harness, which then owns the transport and job store.</summary>
    public static bool IsRegistered(IServiceCollection services) => services.Any(d => d.ServiceType == typeof(TestHarnessRegistration));
}
