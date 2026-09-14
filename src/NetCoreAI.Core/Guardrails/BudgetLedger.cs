using System.Collections.Concurrent;

namespace NetCoreAI.Guardrails;

/// <summary>What a session, a caller and an agent have spent.</summary>
internal interface IBudgetLedger
{
    /// <summary>Tokens a conversation has used so far.</summary>
    long SessionTokens(string sessionId);

    /// <summary>Tokens a caller has used in the last 24 hours.</summary>
    long UserTokensToday(string userId);

    /// <summary>Estimated cost an agent has run up in the last 24 hours.</summary>
    decimal AgentCostToday(string agentId);

    /// <summary>Adds what a finished run used.</summary>
    void Record(string agentId, string? sessionId, string? userId, long tokens, decimal cost);
}

/// <summary>
/// A running total per session, caller and agent.
/// </summary>
/// <remarks>
/// In memory, and therefore per process and lost on restart — the same bargain as the API key rate
/// limiter, and stated in <see cref="BudgetPolicy"/> so nobody has to read this file to find it out.
/// The alternative, a row written and summed per run, turns every agent call into a database round trip
/// for a number whose purpose is to stop a loop rather than to bill anyone. A host that needs real
/// accounting has the run traces, which carry tokens and cost per run and survive a restart.
/// </remarks>
internal sealed class BudgetLedger : IBudgetLedger
{
    private const int Window = 24;

    private readonly ConcurrentDictionary<string, long> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<Entry>> _users = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<Entry>> _agents = new(StringComparer.Ordinal);

    private readonly record struct Entry(DateTimeOffset At, long Tokens, decimal Cost);

    public long SessionTokens(string sessionId) => _sessions.GetValueOrDefault(sessionId);

    public long UserTokensToday(string userId) => (long)Sum(_users, userId, e => e.Tokens);

    public decimal AgentCostToday(string agentId) => Sum(_agents, agentId, e => e.Cost);

    public void Record(string agentId, string? sessionId, string? userId, long tokens, decimal cost)
    {
        if (sessionId is { Length: > 0 })
        {
            _sessions.AddOrUpdate(sessionId, tokens, (_, existing) => existing + tokens);
        }

        var entry = new Entry(DateTimeOffset.UtcNow, tokens, cost);
        if (userId is { Length: > 0 })
        {
            Add(_users, userId, entry);
        }

        if (agentId is { Length: > 0 })
        {
            Add(_agents, agentId, entry);
        }
    }

    private static void Add(ConcurrentDictionary<string, List<Entry>> map, string key, Entry entry)
    {
        var list = map.GetOrAdd(key, _ => []);
        lock (list)
        {
            list.Add(entry);
            Prune(list);
        }
    }

    private static decimal Sum(ConcurrentDictionary<string, List<Entry>> map, string key, Func<Entry, decimal> select)
    {
        if (key is not { Length: > 0 } || !map.TryGetValue(key, out var list))
        {
            return 0;
        }

        lock (list)
        {
            Prune(list);
            return list.Sum(select);
        }
    }

    /// <summary>Drops what has fallen out of the window, which is also what keeps the list from growing without bound.</summary>
    private static void Prune(List<Entry> list)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-Window);
        var keep = list.FindIndex(e => e.At >= cutoff);
        if (keep > 0)
        {
            list.RemoveRange(0, keep);
        }
        else if (keep < 0 && list.Count > 0)
        {
            list.Clear();
        }
    }
}
