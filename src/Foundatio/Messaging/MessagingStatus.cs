using System;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Messaging.Transport;
using Foundatio.Utility;

namespace Foundatio.Messaging;

/// <summary>
/// What this process's message handlers are doing, one entry per source they receive from. Registered by
/// <c>AddFoundatioMessaging</c>; Foundatio.Extensions.Hosting turns it into a health check.
/// </summary>
public sealed class MessagingStatus
{
    private readonly MessagingRuntime _runtime;
    private readonly Lazy<IReadOnlyList<MessageSource>> _registeredSources;

    internal MessagingStatus(MessagingRuntime runtime, MessagingRegistrations registrations)
    {
        _runtime = runtime;
        _registeredSources = new Lazy<IReadOnlyList<MessageSource>>(() => registrations.Handlers.Select(runtime.GetSource).Distinct().ToList());
    }

    /// <summary>
    /// The registered handlers' sources, plus runtime subscriptions. Empty when this process only sends. Sources are
    /// <see cref="ProcessingState.Starting"/> until the startup gate opens and their receiver connects.
    /// </summary>
    public IReadOnlyList<ProcessingStatus> GetProcessors()
    {
        var processors = _runtime.GetProcessors();
        var result = processors.Select(p => p.GetStatus()).ToList();

        var hostState = _runtime.HostState;
        foreach (var source in _registeredSources.Value)
        {
            if (processors.Any(p => p.Source == source))
                continue;

            result.Add(new ProcessingStatus
            {
                Name = source.ToString(),
                State = hostState == ProcessingState.Running ? ProcessingState.Starting : hostState,
                LastError = hostState == ProcessingState.Faulted ? "Startup failed; message handlers will not run" : null
            });
        }

        return result;
    }
}
