using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NetCoreAI.Hub;

/// <summary>Progress of one file, reported as it transfers.</summary>
/// <param name="BytesDone">Bytes written for this file so far, resumed bytes included.</param>
/// <param name="BytesTotal">Total size when the server declared one.</param>
/// <param name="BytesPerSecond">Rate over the last sample window.</param>
internal readonly record struct FileProgress(long BytesDone, long BytesTotal, double BytesPerSecond);

/// <summary>
/// Downloads one file: resumable, optionally in parallel ranges, rate-limited and SHA-256 verified.
/// </summary>
/// <remarks>
/// Bytes land in a "<c>.part</c>" file beside the destination, with a JSON sidecar recording the size and
/// validator the transfer started from. A restart resumes from the sidecar when the server still reports the
/// same file; when it does not, the part file is discarded rather than silently producing a corrupt model.
/// </remarks>
internal sealed class FileDownloader(
    IHttpClientFactory httpClientFactory,
    BandwidthLimiter bandwidth,
    IOptionsMonitor<NetCoreAIOptions> options,
    ILogger<FileDownloader> logger)
{
    private const int BufferSize = 128 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Downloads <paramref name="location"/> to <paramref name="destination"/>, resuming when a part file is
    /// present. Throws <see cref="NetCoreAIException"/> when the transfer or the hash check fails.
    /// </summary>
    public async Task DownloadAsync(
        HubDownloadLocation location,
        string destination,
        Action<FileProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var part = destination + ".part";
        var sidecar = part + ".json";

        var head = await ProbeAsync(location, cancellationToken).ConfigureAwait(false);
        var resumeFrom = ResumePosition(part, sidecar, head);

        if (head.Length is { } total && resumeFrom == total && total > 0)
        {
            // Everything was already fetched before the process stopped; only verification is left.
            await FinishAsync(part, destination, location.Sha256 ?? head.Sha256, cancellationToken).ConfigureAwait(false);
            return;
        }

        WriteSidecar(sidecar, head);

        var chunks = PlanChunks(head, resumeFrom);
        var reporter = new ProgressReporter(resumeFrom, head.Length ?? 0, progress);

        if (chunks.Count <= 1)
        {
            // Only a resume needs a range: asking for "the whole file as a range" makes servers that ignore
            // ranges look like they are misbehaving, when in fact a plain GET is all this transfer needs.
            var end = resumeFrom > 0 && head.Length is { } length ? length - 1 : (long?)null;
            await DownloadRangeAsync(location, part, resumeFrom, end, append: true, reporter, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await DownloadInParallelAsync(location, part, chunks, reporter, cancellationToken).ConfigureAwait(false);
        }

        await FinishAsync(part, destination, location.Sha256 ?? head.Sha256, cancellationToken).ConfigureAwait(false);
        Delete(sidecar);
    }

    /// <summary>Asks the server for the size, whether ranges are supported, and any validator it offers.</summary>
    private async Task<RemoteFile> ProbeAsync(HubDownloadLocation location, CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, location.Url);
        Apply(request, location.Headers);

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Some mirrors answer HEAD with 405: fall back to an unknown-length streaming download.
                logger.LogDebug("HEAD {Url} returned {Status}; downloading without a size.", location.Url, response.StatusCode);
                return new RemoteFile(null, false, null, null);
            }

            var length = location.SizeBytes ?? response.Content.Headers.ContentLength;
            var acceptsRanges = response.Headers.AcceptRanges.Contains("bytes");
            return new RemoteFile(length, acceptsRanges, response.Headers.ETag?.Tag, location.Sha256 ?? HashFrom(response));
        }
        catch (HttpRequestException ex)
        {
            throw new NetCoreAIException($"Could not reach {location.Url.Host}: {ex.Message}", ex);
        }
    }

    /// <summary>Hugging Face publishes the LFS SHA-256 in a header on the resolved file.</summary>
    private static string? HashFrom(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-linked-etag", out var linked) && linked.FirstOrDefault()?.Trim('"') is { Length: 64 } sha
            ? sha
            : null;

    /// <summary>How many bytes of a previous attempt can be kept, after checking the file has not changed.</summary>
    private long ResumePosition(string part, string sidecar, RemoteFile head)
    {
        if (!File.Exists(part))
        {
            return 0;
        }

        var existing = new FileInfo(part).Length;
        if (existing == 0)
        {
            return 0;
        }

        if (!head.AcceptsRanges)
        {
            logger.LogInformation("The server does not support resuming, so the partial download is being restarted.");
            Delete(part);
            return 0;
        }

        if (ReadSidecar(sidecar) is { } saved && !saved.Matches(head))
        {
            logger.LogWarning("The remote file changed since the partial download started; fetching it again from the beginning.");
            Delete(part);
            return 0;
        }

        if (head.Length is { } total && existing > total)
        {
            Delete(part);
            return 0;
        }

        return existing;
    }

    /// <summary>Splits what is left into ranges, or returns a single chunk when parallelism cannot help.</summary>
    private List<Chunk> PlanChunks(RemoteFile head, long resumeFrom)
    {
        var parallelism = Math.Clamp(options.CurrentValue.Network.ParallelDownloadChunks, 1, 16);
        if (!head.AcceptsRanges || head.Length is not { } total || parallelism == 1)
        {
            return [new Chunk(resumeFrom, null)];
        }

        var remaining = total - resumeFrom;

        // Splitting is only worth a round trip on files big enough for each part to carry real weight.
        const long minimumChunk = 8L * 1024 * 1024;
        if (remaining < minimumChunk * 2)
        {
            return [new Chunk(resumeFrom, total - 1)];
        }

        var count = (int)Math.Min(parallelism, remaining / minimumChunk);
        var size = remaining / count;
        var chunks = new List<Chunk>(count);
        for (var i = 0; i < count; i++)
        {
            var start = resumeFrom + (i * size);
            var end = i == count - 1 ? total - 1 : start + size - 1;
            chunks.Add(new Chunk(start, end));
        }

        return chunks;
    }

    private async Task DownloadInParallelAsync(HubDownloadLocation location, string part, List<Chunk> chunks, ProgressReporter reporter, CancellationToken cancellationToken)
    {
        // Pre-size the file so every range can write straight to its own offset.
        var total = chunks[^1].End!.Value + 1;
        using (var file = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
        {
            if (file.Length < total)
            {
                file.SetLength(total);
            }
        }

        await Task.WhenAll(chunks.Select(chunk =>
            DownloadRangeAsync(location, part, chunk.Start, chunk.End, append: false, reporter, cancellationToken))).ConfigureAwait(false);
    }

    /// <summary>Transfers one byte range (or the whole file when <paramref name="end"/> is null).</summary>
    private async Task DownloadRangeAsync(
        HubDownloadLocation location,
        string part,
        long start,
        long? end,
        bool append,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, location.Url);
        Apply(request, location.Headers);
        if (start > 0 || end is not null)
        {
            request.Headers.Range = new RangeHeaderValue(start, end);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The part file already holds everything this range covers.
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new NetCoreAIException($"Download of {location.Url} failed with {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        if ((start > 0 || end is not null) && response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new NetCoreAIException(
                $"{location.Url.Host} ignored the byte range and sent the whole file, so the download cannot be resumed safely. Delete the .part file and start again.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = append
            ? new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, BufferSize)
            : new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, BufferSize);

        if (!append)
        {
            target.Seek(start, SeekOrigin.Begin);
        }

        var buffer = new byte[BufferSize];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await bandwidth.ConsumeAsync(read, cancellationToken).ConfigureAwait(false);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            reporter.Add(read);
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Verifies the hash when one is known, then moves the part file into place.</summary>
    private static async Task FinishAsync(string part, string destination, string? expectedSha256, CancellationToken cancellationToken)
    {
        if (expectedSha256 is { Length: 64 })
        {
            var actual = await ComputeSha256Async(part, cancellationToken).ConfigureAwait(false);
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                Delete(part);
                throw new NetCoreAIException(
                    $"The downloaded file did not match its published SHA-256 and was deleted. Expected {expectedSha256}, got {actual}. Try the download again; if it keeps failing, the mirror may be serving a corrupt copy.");
            }
        }

        File.Move(part, destination, overwrite: true);
    }

    internal static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private HttpClient CreateClient() => httpClientFactory.CreateClient(NetCoreAIHttp.DownloadClient);

    private static void Apply(HttpRequestMessage request, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private void WriteSidecar(string sidecar, RemoteFile head)
    {
        try
        {
            File.WriteAllText(sidecar, JsonSerializer.Serialize(head, Json));
        }
        catch (IOException ex)
        {
            // Without the sidecar a resume simply restarts, which is not worth failing the download over.
            logger.LogDebug(ex, "Could not write the resume sidecar {Path}.", sidecar);
        }
    }

    private static RemoteFile? ReadSidecar(string sidecar)
    {
        try
        {
            return File.Exists(sidecar) ? JsonSerializer.Deserialize<RemoteFile>(File.ReadAllText(sidecar), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private readonly record struct Chunk(long Start, long? End);

    /// <summary>What the server said about the file when the transfer started.</summary>
    internal sealed record RemoteFile(long? Length, bool AcceptsRanges, string? ETag, string? Sha256)
    {
        /// <summary>True when the file on the server still looks like the one a part file was started from.</summary>
        public bool Matches(RemoteFile other) =>
            Length == other.Length
            && (ETag is null || other.ETag is null || string.Equals(ETag, other.ETag, StringComparison.Ordinal))
            && (Sha256 is null || other.Sha256 is null || string.Equals(Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Accumulates bytes across parallel ranges and samples the rate about once a second.</summary>
    private sealed class ProgressReporter(long initial, long total, Action<FileProgress>? callback)
    {
        private readonly long _startTicks = Environment.TickCount64;
        private long _done = initial;
        private long _lastReportTicks;
        private long _lastReportBytes = initial;

        public void Add(int bytes)
        {
            var done = Interlocked.Add(ref _done, bytes);
            if (callback is null)
            {
                return;
            }

            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastReportTicks);
            var elapsed = now - (last == 0 ? _startTicks : last);
            if (elapsed < 1000)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastReportTicks, now, last) != last)
            {
                return;
            }

            var since = done - Interlocked.Exchange(ref _lastReportBytes, done);
            callback(new FileProgress(done, total, since * 1000.0 / Math.Max(1, elapsed)));
        }

        public string Describe() => string.Create(CultureInfo.InvariantCulture, $"{_done}/{total}");
    }
}
