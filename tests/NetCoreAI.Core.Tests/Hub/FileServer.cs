using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace NetCoreAI.Core.Tests.Hub;

/// <summary>
/// A tiny in-process file server for download tests: serves bytes with or without range support, and can
/// be told to drop a connection part-way so resume can be exercised against real HTTP rather than a mock.
/// </summary>
internal sealed class FileServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FileServer(WebApplication app, byte[] content)
    {
        _app = app;
        Content = content;
        Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    public byte[] Content { get; }

    public string Sha256 { get; }

    public Uri FileUrl => new($"{_app.Urls.First()}/model.bin");

    /// <summary>When set, the server closes the connection after this many bytes of each response.</summary>
    public int? TruncateAfterBytes { get; set; }

    /// <summary>Whether the server advertises and honours byte ranges.</summary>
    public bool SupportsRanges { get; set; } = true;

    /// <summary>Requests received, so tests can prove a resume asked for a range rather than the whole file.</summary>
    public List<string?> RangeHeaders { get; } = [];

    public static async Task<FileServer> StartAsync(int sizeBytes)
    {
        var content = new byte[sizeBytes];
        RandomNumberGenerator.Fill(content);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var server = new FileServer(app, content);

        app.MapMethods("/model.bin", ["HEAD"], (HttpContext http) =>
        {
            http.Response.ContentLength = server.Content.Length;
            if (server.SupportsRanges)
            {
                http.Response.Headers.AcceptRanges = "bytes";
            }

            http.Response.Headers.ETag = "\"test-etag\"";
            return Task.CompletedTask;
        });

        app.MapGet("/model.bin", async (HttpContext http) =>
        {
            lock (server.RangeHeaders)
            {
                server.RangeHeaders.Add(http.Request.Headers.Range.ToString() is { Length: > 0 } r ? r : null);
            }

            var (offset, length) = server.ResolveRange(http);
            if (offset < 0)
            {
                http.Response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                return;
            }

            http.Response.Headers.ETag = "\"test-etag\"";
            if (server.SupportsRanges)
            {
                http.Response.Headers.AcceptRanges = "bytes";
            }

            if (http.Request.Headers.Range.Count > 0 && server.SupportsRanges)
            {
                http.Response.StatusCode = (int)HttpStatusCode.PartialContent;
                http.Response.Headers.ContentRange = $"bytes {offset}-{offset + length - 1}/{server.Content.Length}";
            }

            var send = server.TruncateAfterBytes is { } limit ? Math.Min(length, limit) : length;
            http.Response.ContentLength = length;
            await http.Response.Body.WriteAsync(server.Content.AsMemory(offset, send), http.RequestAborted);
            await http.Response.Body.FlushAsync(http.RequestAborted);

            if (send < length)
            {
                // Stop short of the declared length: the client sees a truncated body, as on a dropped connection.
                http.Abort();
            }
        });

        await app.StartAsync();
        return server;
    }

    private (int Offset, int Length) ResolveRange(HttpContext http)
    {
        if (!SupportsRanges || http.Request.Headers.Range.Count == 0)
        {
            return (0, Content.Length);
        }

        var value = http.Request.Headers.Range.ToString();
        var span = value["bytes=".Length..];
        var parts = span.Split('-', 2);
        var from = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        var to = parts.Length > 1 && parts[1].Length > 0
            ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)
            : Content.Length - 1;

        return from >= Content.Length ? (-1, 0) : (from, Math.Min(to, Content.Length - 1) - from + 1);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
