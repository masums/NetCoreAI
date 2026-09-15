namespace NetCoreAI.Hub;

/// <summary>
/// Whether NetCoreAI may call a given address.
/// </summary>
/// <remarks>
/// <para>
/// One definition of "allowed", used by every place that asks. There used to be two — the HTTP handler
/// understood <c>*.example.com</c> and the provider check did not — and a residency policy that means two
/// different things in two places is a policy with a hole in it by construction.
/// </para>
/// <para>
/// This is the data-residency switch. With <c>Network.OfflineMode</c> on, nothing leaves the host except
/// to the hosts named in <c>Network.AllowedHosts</c>: an internal Hugging Face mirror, a model gateway
/// inside the same jurisdiction, a proxy that is itself audited. Off, it allows everything and costs a
/// boolean read.
/// </para>
/// </remarks>
public static class EgressPolicy
{
    /// <summary>Whether a request to this address may be made.</summary>
    public static bool IsAllowed(Uri? address, NetworkOptions network)
    {
        ArgumentNullException.ThrowIfNull(network);

        if (!network.OfflineMode)
        {
            return true;
        }

        if (address is null)
        {
            // No address is not an address that passed the check.
            return false;
        }

        // This machine is not somewhere else. A local Ollama, a sidecar, a loopback mirror: none of them
        // is data leaving anywhere, and refusing them would make the switch unusable exactly where it is
        // most wanted.
        if (address.IsLoopback)
        {
            return true;
        }

        foreach (var allowed in network.AllowedHosts)
        {
            if (Matches(allowed, address.Host))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a base URL may be called. An unparseable one is refused rather than assumed local.</summary>
    public static bool IsAllowed(string? baseUrl, NetworkOptions network) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            ? IsAllowed(uri, network)
            : !(network ?? throw new ArgumentNullException(nameof(network))).OfflineMode;

    /// <summary>The sentence a caller sees when a call is refused.</summary>
    public static string Refusal(string host) =>
        $"Offline mode is on, so NetCoreAI did not call {host}. Turn it off in Settings → Network, or add the host to Network.AllowedHosts if it is an approved mirror.";

    /// <summary>
    /// Whether an allow-list entry covers a host.
    /// </summary>
    /// <remarks>
    /// Exact, or a <c>*.</c> prefix covering subdomains. The wildcard keeps its leading dot when it is
    /// compared, so <c>*.example.com</c> covers <c>mirror.example.com</c> and not
    /// <c>evil-example.com</c> — a plain suffix match would let the second one through, which is the usual
    /// way an allow-list turns out to allow everything.
    /// </remarks>
    private static bool Matches(string allowed, string host) =>
        allowed.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(allowed[1..], StringComparison.OrdinalIgnoreCase)
            : string.Equals(allowed, host, StringComparison.OrdinalIgnoreCase);
}
