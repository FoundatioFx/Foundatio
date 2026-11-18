using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Security;
using RabbitMQ.Client;

namespace Foundatio.Utility;

/// <summary>Creates validated RabbitMQ endpoints with explicit TLS identities.</summary>
public static class RabbitMQEndpointResolver
{
    /// <summary>Uses replacement hosts when supplied, or the factory URI endpoint otherwise.</summary>
    /// <remarks>Each TLS endpoint validates its own hostname and certificate chain. Client certificates are preserved;
    /// permissive policy errors from the URI are overridden and custom server-validation callbacks are rejected. Empty host entries are ignored;
    /// malformed endpoints are rejected. Host entries may include a port, with brackets required around IPv6 when specifying one.</remarks>
    public static List<AmqpTcpEndpoint> CreateEndpoints(ConnectionFactory factory, IList<string>? hosts = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (factory.Ssl.CertificateValidationCallback is not null)
            throw new ArgumentException("Endpoint resolution requires default server certificate validation.", nameof(factory));

        var connectionUri = factory.Uri;
        int defaultPort = factory.Ssl.Enabled ? 5671 : 5672;
        var endpoints = new List<AmqpTcpEndpoint>(hosts is { Count: > 0 } ? hosts.Count : 1);
        if (hosts is not { Count: > 0 })
        {
            endpoints.Add(CreateEndpoint(factory, connectionUri.DnsSafeHost,
                connectionUri.Port < 0 ? defaultPort : connectionUri.Port,
                nameof(factory)));
            return endpoints;
        }

        var seenEndpoints = new HashSet<string>(hosts.Count, StringComparer.OrdinalIgnoreCase);
        foreach (string host in hosts)
        {
            if (String.IsNullOrWhiteSpace(host))
                continue;

            var (hostname, port) = ParseHost(host.Trim(), defaultPort);
            var endpoint = CreateEndpoint(factory, hostname, port, nameof(hosts));
            if (seenEndpoints.Add(FormattableString.Invariant($"{endpoint.HostName}:{endpoint.Port}")))
                endpoints.Add(endpoint);
        }

        if (endpoints is { Count: 0 })
            throw new ArgumentException("Hosts must contain at least one nonempty endpoint.", nameof(hosts));

        return endpoints;
    }

    private static AmqpTcpEndpoint CreateEndpoint(ConnectionFactory factory, string hostname, int port, string parameterName)
    {
        if (Uri.CheckHostName(hostname) == UriHostNameType.Unknown)
            throw new ArgumentException("An endpoint must contain a valid hostname or IP address.", parameterName);
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(parameterName, port, "The endpoint port must be from 1 to 65535.");

        // Explicit endpoints do not inherit ConnectionFactory.Ssl. Give every endpoint its own
        // policy and verify the name actually used to connect, including replacement hosts.
        var ssl = new SslOption(hostname, enabled: factory.Ssl.Enabled)
        {
            Version = factory.Ssl.Version,
            AcceptablePolicyErrors = SslPolicyErrors.None,
            CheckCertificateRevocation = factory.Ssl.CheckCertificateRevocation,
            CertPath = factory.Ssl.CertPath,
            CertPassphrase = factory.Ssl.CertPassphrase,
            Certs = factory.Ssl.Certs,
            CertificateSelectionCallback = factory.Ssl.CertificateSelectionCallback,
            ClientCertificateContext = factory.Ssl.ClientCertificateContext
        };

        return new AmqpTcpEndpoint(hostname, port, ssl, factory.MaxInboundMessageBodySize);
    }

    private static (string Hostname, int Port) ParseHost(string host, int defaultPort)
    {
        if (host.StartsWith('['))
        {
            int closeBracket = host.IndexOf(']');
            if (closeBracket <= 1)
                throw InvalidHost();

            string hostname = host[1..closeBracket];
            if (Uri.CheckHostName(hostname) != UriHostNameType.IPv6)
                throw InvalidHost();

            if (closeBracket == host.Length - 1)
                return (hostname, defaultPort);

            if (host[closeBracket + 1] != ':')
                throw InvalidHost();

            return (hostname, ParsePort(host[(closeBracket + 2)..]));
        }

        int colonIndex = host.LastIndexOf(':');
        if (colonIndex < 0 || host.IndexOf(':') != colonIndex)
            return (host, defaultPort);

        return (host[..colonIndex], ParsePort(host[(colonIndex + 1)..]));
    }

    private static int ParsePort(string value)
    {
        if (!Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int port))
            throw InvalidHost();
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException("hosts", port, "The endpoint port must be from 1 to 65535.");

        return port;
    }

    private static ArgumentException InvalidHost() => new(
        "Each Hosts entry must be a hostname or IP address, optionally followed by a port from 1 to 65535. Use brackets for an IPv6 address with a port.",
        "hosts");
}
