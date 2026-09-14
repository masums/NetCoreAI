namespace NetCoreAI.Tools.BuiltIn;

/// <summary>
/// What the built-in tools are allowed to do.
/// </summary>
/// <remarks>
/// Everything with a reach outside this process is off until it is configured. A model that can fetch any
/// URL is a request-forgery hole with a friendly name, and one that can run any SQL is a database console;
/// both are useful and both need a person to say where they may point first.
/// </remarks>
public sealed class BuiltInToolOptions
{
    /// <summary>Today's date and the current time. No reach outside the process, so on by default.</summary>
    public bool DateTime { get; set; } = true;

    /// <summary>Arithmetic. No reach outside the process, so on by default.</summary>
    public bool Calculator { get; set; } = true;

    /// <summary>Searching the host's knowledge bases. On by default; retrieval already filters by caller.</summary>
    public bool KnowledgeSearch { get; set; } = true;

    /// <summary>
    /// Hosts the fetch tool may reach, as exact host names. Empty leaves the tool unregistered.
    /// </summary>
    /// <remarks>
    /// An allow-list rather than a block-list. A block-list has to anticipate every internal address worth
    /// protecting — including the cloud metadata endpoints that hand out credentials — and it only has to
    /// be wrong once.
    /// </remarks>
    public IList<string> FetchAllowedHosts { get; } = [];

    /// <summary>Most bytes the fetch tool will read from one response.</summary>
    public int FetchMaxBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Connection the SQL tool reads from, as an ADO.NET provider name and connection string. Null leaves
    /// the tool unregistered.
    /// </summary>
    public SqlToolConnection? Sql { get; set; }
}

/// <summary>Where the read-only SQL tool points, and how far it may go.</summary>
/// <param name="ProviderName">Invariant name of a registered ADO.NET provider, e.g. <c>Npgsql</c>.</param>
/// <param name="ConnectionString">
/// How to reach the database. Give it an account that can only read: the tool refuses anything but a
/// SELECT, but a permission the account does not have is a stronger guarantee than a string check.
/// </param>
public sealed record SqlToolConnection(string ProviderName, string ConnectionString)
{
    /// <summary>Rows returned at most. A model cannot read ten thousand rows and will try.</summary>
    public int MaxRows { get; init; } = 50;

    public int TimeoutSeconds { get; init; } = 15;

    /// <summary>
    /// Tables the query may name. Empty allows any the account can read.
    /// </summary>
    /// <remarks>
    /// Worth setting even with a read-only account: "read-only" and "may read the password hashes" are
    /// not mutually exclusive.
    /// </remarks>
    public IReadOnlyList<string> AllowedTables { get; init; } = [];
}
