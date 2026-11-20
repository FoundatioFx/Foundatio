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

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Reads queues in the connection string's virtual host from the RabbitMQ management API, with the credentials of the
/// endpoint's URI or else those of the connection string.
/// </summary>
internal sealed class RabbitMQManagementClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly string _virtualHost = "/";

    public RabbitMQManagementClient(Uri endpoint, string? connectionString) : this(endpoint, connectionString, null)
    {
    }

    /// <param name="endpoint">The management API's base URI.</param>
    /// <param name="connectionString">The AMQP connection string, for its virtual host and credentials.</param>
    /// <param name="handler">Sends the requests instead of the default handler, for tests.</param>
    internal RabbitMQManagementClient(Uri endpoint, string? connectionString, HttpMessageHandler? handler)
    {
        Uri? connectionUri = Uri.TryCreate(connectionString, UriKind.Absolute, out var parsed) ? parsed : null;
        if (connectionUri is not null)
            _virtualHost = new ConnectionFactory { Uri = connectionUri }.VirtualHost;

        string userInfo = endpoint.UserInfo.Length > 0 ? endpoint.UserInfo : connectionUri?.UserInfo ?? String.Empty;
        _http = new HttpClient(handler ?? new HttpClientHandler()) { BaseAddress = new Uri(new UriBuilder(endpoint) { UserName = String.Empty, Password = String.Empty }.Uri.AbsoluteUri.TrimEnd('/') + "/"), Timeout = RequestTimeout };
        if (userInfo.Length > 0)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(userInfo))));
    }

    /// <summary>The queue's details, or null when it does not exist.</summary>
    public Task<JsonDocument?> GetQueueAsync(string queue, CancellationToken cancellationToken)
    {
        return GetAsync($"api/queues/{Uri.EscapeDataString(_virtualHost)}/{Uri.EscapeDataString(queue)}", cancellationToken);
    }

    /// <summary>
    /// The details of up to <paramref name="count"/> queues whose names match the regular expression
    /// <paramref name="pattern"/>, ordered by <paramref name="sortBy"/>, highest first when <paramref name="descending"/>
    /// is set, and only their <paramref name="columns"/> (comma-separated fields) when it is set, as a page whose
    /// <c>items</c> lists them and whose <c>filtered_count</c> counts every queue that matches. Ordering by a statistic
    /// such as <c>messages_dlx</c> makes the broker read the statistics of every queue in the virtual host, whatever the
    /// pattern matches, and choosing columns as well makes that slower still; ordering by <c>name</c> does neither.
    /// </summary>
    public async Task<JsonDocument> GetQueuesAsync(string pattern, int count, string sortBy, bool descending, string? columns, CancellationToken cancellationToken)
    {
        string path = $"api/queues/{Uri.EscapeDataString(_virtualHost)}?page=1&page_size={count}&use_regex=true&name={Uri.EscapeDataString(pattern)}" +
            $"&sort={Uri.EscapeDataString(sortBy)}{(descending ? "&sort_reverse=true" : String.Empty)}{(columns is null ? String.Empty : $"&columns={Uri.EscapeDataString(columns)}")}";
        return await GetAsync(path, cancellationToken).AnyContext() ?? throw new HttpRequestException($"The management API has no virtual host {_virtualHost}.");
    }

    public void Dispose() => _http.Dispose();

    private async Task<JsonDocument?> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken).AnyContext();
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).AnyContext();
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).AnyContext();
    }
}
