using Microsoft.Extensions.Options;
using NetCoreAI.Knowledge;

namespace NetCoreAI.Tenancy;

/// <summary>What a tenant is allowed one more of.</summary>
public enum QuotaKind
{
    Agent,
    Tool,
    KnowledgeBase,
    Document,
    UploadBytes,
}

/// <summary>Checking a tenant against its limits.</summary>
public interface ITenantQuotas
{
    /// <summary>
    /// Refuses the operation when it would take the current tenant past a limit.
    /// </summary>
    /// <param name="kind">What is about to be created.</param>
    /// <param name="adding">How many, or how many bytes.</param>
    /// <param name="cancellationToken">Cancels the count.</param>
    /// <exception cref="NetCoreAIException">The limit would be exceeded, said in a sentence a user can act on.</exception>
    Task EnsureRoomForAsync(QuotaKind kind, long adding = 1, CancellationToken cancellationToken = default);

    /// <summary>Why this tenant may not run anything more today, or null.</summary>
    string? CheckDailyBudget();

    /// <summary>Everything the current tenant is using, against what it may use.</summary>
    Task<IReadOnlyList<QuotaUsage>> UsageAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Counts what a tenant has and refuses the next one when it would be too many.
/// </summary>
/// <remarks>
/// <para>
/// Counted from the store rather than from a running total. The store is already scoped to the tenant, so
/// a count is a filtered query the database answers from an index, and it cannot drift out of step with
/// what is actually there — which a cached counter does the first time a row is removed by anything other
/// than the path that maintains it.
/// </para>
/// <para>
/// Checked before the work rather than after. A quota enforced once the document is embedded has already
/// spent the embedding call.
/// </para>
/// </remarks>
internal sealed class TenantQuotas(
    IMetadataStore store,
    ITenantAccessor tenants,
    ITenantService tenantService,
    NetCoreAI.Guardrails.IBudgetLedger ledger,
    IOptionsMonitor<NetCoreAIOptions> options) : ITenantQuotas
{
    public async Task EnsureRoomForAsync(QuotaKind kind, long adding = 1, CancellationToken cancellationToken = default)
    {
        var quota = await QuotaAsync(cancellationToken).ConfigureAwait(false);
        var limit = Limit(quota, kind);
        if (limit <= 0)
        {
            // No limit set. Nothing is counted, so a host with no quotas pays nothing for having the
            // feature — which is the only way a default stays a default.
            return;
        }

        var used = await UsedAsync(kind, cancellationToken).ConfigureAwait(false);
        if (used + adding > limit)
        {
            throw new NetCoreAIException(Refusal(kind, used, limit));
        }
    }

    public string? CheckDailyBudget()
    {
        var tenant = tenants.Current;
        var quota = QuotaAsync(CancellationToken.None).GetAwaiter().GetResult();

        if (quota.MaxTokensPerDay > 0 && ledger.TenantTokensToday(tenant) >= quota.MaxTokensPerDay)
        {
            return $"This tenant has used its daily budget of {quota.MaxTokensPerDay} tokens.";
        }

        if (quota.MaxCostPerDay > 0 && ledger.TenantCostToday(tenant) >= quota.MaxCostPerDay)
        {
            return "This tenant has reached its spending limit for today.";
        }

        return null;
    }

    public async Task<IReadOnlyList<QuotaUsage>> UsageAsync(CancellationToken cancellationToken = default)
    {
        var quota = await QuotaAsync(cancellationToken).ConfigureAwait(false);
        var usage = new List<QuotaUsage>();

        foreach (var kind in Enum.GetValues<QuotaKind>())
        {
            usage.Add(new QuotaUsage(
                Name(kind),
                await UsedAsync(kind, cancellationToken).ConfigureAwait(false),
                Limit(quota, kind)));
        }

        usage.Add(new QuotaUsage("tokens today", ledger.TenantTokensToday(tenants.Current), quota.MaxTokensPerDay));
        return usage;
    }

    private async Task<TenantQuota> QuotaAsync(CancellationToken cancellationToken) =>
        (await tenantService.GetAsync(tenants.Current, cancellationToken).ConfigureAwait(false))?.Quota ?? new TenantQuota();

    private static long Limit(TenantQuota quota, QuotaKind kind) => kind switch
    {
        QuotaKind.Agent => quota.MaxAgents,
        QuotaKind.Tool => quota.MaxTools,
        QuotaKind.KnowledgeBase => quota.MaxKnowledgeBases,
        QuotaKind.Document => quota.MaxDocuments,
        _ => quota.MaxUploadBytes,
    };

    private static string Name(QuotaKind kind) => kind switch
    {
        QuotaKind.Agent => "agents",
        QuotaKind.Tool => "tools",
        QuotaKind.KnowledgeBase => "knowledge bases",
        QuotaKind.Document => "documents",
        _ => "uploaded bytes",
    };

    private async Task<long> UsedAsync(QuotaKind kind, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case QuotaKind.Agent:
                return (await store.Agents.ListAsync(cancellationToken).ConfigureAwait(false)).Count;

            case QuotaKind.Tool:
                return (await store.Tools.ListAsync(cancellationToken).ConfigureAwait(false)).Count;

            case QuotaKind.KnowledgeBase:
                return (await store.Knowledge.ListAsync(cancellationToken).ConfigureAwait(false)).Count;

            case QuotaKind.Document:
            {
                var total = 0L;
                foreach (var knowledgeBase in await store.Knowledge.ListAsync(cancellationToken).ConfigureAwait(false))
                {
                    total += (await store.Knowledge.ListDocumentsAsync(knowledgeBase.Id, null, cancellationToken).ConfigureAwait(false)).Count;
                }

                return total;
            }

            default:
                return UploadBytes();
        }
    }

    /// <summary>
    /// What this tenant's uploaded files occupy.
    /// </summary>
    /// <remarks>
    /// Measured from the disk rather than summed from the database. The files are the thing the quota is
    /// about, and a total kept in a column is wrong the first time one is deleted by hand.
    /// </remarks>
    private long UploadBytes()
    {
        var root = FileDataSource.UploadRoot(options.CurrentValue.DataDirectory, tenants.Current);
        if (!Directory.Exists(root))
        {
            return 0;
        }

        try
        {
            return new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (IOException)
        {
            // A file moving under the walk is not a reason to refuse an upload.
            return 0;
        }
    }

    private static string Refusal(QuotaKind kind, long used, long limit) => kind switch
    {
        QuotaKind.UploadBytes =>
            $"This tenant may store {limit} bytes of uploaded files and is using {used}. Delete some documents, or ask for a larger allowance.",
        _ =>
            $"This tenant may have {limit} {Name(kind)} and already has {used}. Delete one, or ask for a larger allowance.",
    };
}
