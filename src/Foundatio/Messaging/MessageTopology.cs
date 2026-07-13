using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Messaging;

public interface IMessageTopology
{
    IReadOnlyList<DestinationDeclaration> GetDeclarations();
    Task EnsureAsync(CancellationToken cancellationToken = default);
    Task ValidateAsync(CancellationToken cancellationToken = default);
}

public sealed class MessageTopology : IMessageTopology
{
    private readonly IMessageTransport _transport;
    private readonly MessageRoutingOptions _options;

    public MessageTopology(IMessageTransport transport, MessageRoutingOptions options)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public IReadOnlyList<DestinationDeclaration> GetDeclarations()
    {
        return _options.GetTopologyDeclarations();
    }

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        var declarations = GetDeclarations();
        if (declarations.Count == 0)
            return;

        if (_transport is not ISupportsProvisioning provisioning)
            throw new NotSupportedException($"Transport \"{_transport.GetType().Name}\" does not support topology provisioning.");

        await provisioning.EnsureAsync(declarations, cancellationToken).AnyContext();
    }

    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        var declarations = GetDeclarations();
        if (declarations.Count == 0)
            return;

        if (_transport is not ISupportsProvisioning provisioning)
            throw new NotSupportedException($"Transport \"{_transport.GetType().Name}\" does not support topology validation.");

        var missing = new List<DestinationDeclaration>();
        foreach (var declaration in declarations)
        {
            if (!await provisioning.ExistsAsync(declaration.Address, cancellationToken).AnyContext())
                missing.Add(declaration);
        }

        if (missing.Count > 0)
            throw new InvalidOperationException($"Message topology is missing: {String.Join(", ", missing.Select(d => d.Address.ToString()))}.");
    }
}
