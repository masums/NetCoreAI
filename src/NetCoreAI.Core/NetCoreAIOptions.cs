using Microsoft.AspNetCore.Authorization;

namespace NetCoreAI;

/// <summary>
/// Root options for NetCoreAI. Bound from the "NetCoreAI" configuration section and then from the
/// <c>AddNetCoreAI(o => ...)</c> delegate; UI overrides persisted in the metadata store win at runtime.
/// </summary>
public sealed class NetCoreAIOptions
{
    public const string SectionName = "NetCoreAI";

    /// <summary>Root folder for models, vector data, metadata DB and uploads. Relative paths resolve against the content root.</summary>
    public string DataDirectory { get; set; } = "./netcoreai";

    public DashboardOptions Dashboard { get; set; } = new();

    public ModelsOptions Models { get; set; } = new();

    public NetworkOptions Network { get; set; } = new();

    public ToolOptions Tools { get; set; } = new();

    public ProvidersOptions Providers { get; set; } = new();

    public TelemetryOptions Telemetry { get; set; } = new();

    /// <summary>
    /// Default rules for every agent that does not carry its own. Nothing is switched on here: a
    /// guardrail that fires when nobody asked for it turns a working agent into a broken one.
    /// </summary>
    public NetCoreAI.Guardrails.GuardrailPolicy Guardrails { get; set; } = new();

    public StorageOptions Storage { get; set; } = new();

    public NetCoreAI.Security.AuditOptions Audit { get; set; } = new();
}

/// <summary>What is kept, and for how long.</summary>
public sealed class StorageOptions
{
    /// <summary>
    /// Days a run trace is kept. 0 keeps them forever.
    /// </summary>
    /// <remarks>
    /// Traces carry whatever people asked an agent, which is both the reason to keep them — a wrong answer
    /// cannot be explained without one — and the reason not to keep them indefinitely.
    /// </remarks>
    public int RunRetentionDays { get; set; } = 90;
}

public sealed class DashboardOptions
{
    /// <summary>Path prefix for the dashboard, management API and agent API. Default "/netcoreai".</summary>
    public string Path { get; set; } = "/netcoreai";

    /// <summary>
    /// Authorization policy applied to everything under <see cref="Path"/>. Default deny: when neither this nor
    /// <see cref="AllowAnonymous"/> is set, every request gets 403 with a hint page.
    /// </summary>
    public Action<AuthorizationPolicyBuilder>? Authorization { get; set; }

    /// <summary>Name of an existing host policy to use instead of <see cref="Authorization"/>.</summary>
    public string? PolicyName { get; set; }

    /// <summary>Allow unauthenticated access. Only for local development or already-isolated networks.</summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>Claim type carrying NetCoreAI roles (defaults to the host's role claim).</summary>
    public string? RoleClaimType { get; set; }

    /// <summary>Title shown in the dashboard header.</summary>
    public string Title { get; set; } = "NetCoreAI";
}

public sealed class ModelsOptions
{
    /// <summary>Unload a local model after this idle period; null = never.</summary>
    public TimeSpan? IdleUnloadTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Total RAM+VRAM local models may occupy; null = 80 % of physical RAM + all VRAM.</summary>
    public long? MemoryBudgetBytes { get; set; }

    public ConcurrencyPolicy DefaultConcurrency { get; set; } = ConcurrencyPolicy.SingleSlot;

    public int DefaultPoolSize { get; set; } = 2;

    /// <summary>Preferred execution provider for local backends.</summary>
    public ExecutionProvider ExecutionProvider { get; set; } = ExecutionProvider.Auto;

    public int? Threads { get; set; }

    /// <summary>Default context size requested at load when the descriptor does not specify one.</summary>
    public int DefaultContextSize { get; set; } = 4096;

    /// <summary>Warn in the dashboard when the data directory exceeds this size; null = no warning.</summary>
    public long? StorageQuotaWarningBytes { get; set; }
}

/// <summary>Settings for the tools a model may call.</summary>
public sealed class ToolOptions
{
    /// <summary>
    /// Where this host answers its own requests, for tools invoked by HTTP loopback.
    /// </summary>
    /// <remarks>
    /// Usually worked out from the request that started the run. Set it when the host cannot know its own
    /// reachable address — behind a reverse proxy, or in a container answering on a different port than it
    /// is reached on — or when tools run outside a request, where there is no request to read it from.
    /// </remarks>
    public Uri? BaseAddress { get; set; }
}

public sealed class NetworkOptions
{
    /// <summary>Disables every outbound call (hub browsing, downloads, remote providers except allow-listed).</summary>
    public bool OfflineMode { get; set; }

    public string HuggingFaceEndpoint { get; set; } = "https://huggingface.co";

    /// <summary>Hugging Face token for gated repos. Prefer the NETCOREAI__NETWORK__HUGGINGFACETOKEN environment variable in containers.</summary>
    public string? HuggingFaceToken { get; set; }

    public string? ProxyUrl { get; set; }

    /// <summary>Download bandwidth cap in bytes per second; null = unlimited.</summary>
    public long? BandwidthLimitBytesPerSecond { get; set; }

    public int ParallelDownloadChunks { get; set; } = 4;

    /// <summary>Hosts that stay reachable in offline / data-residency mode (mirrors).</summary>
    public IList<string> AllowedHosts { get; set; } = [];

    /// <summary>URL of the curated "Recommended" manifest; embedded copy is used when unreachable.</summary>
    public string RecommendedManifestUrl { get; set; } = "https://raw.githubusercontent.com/masums/NetCoreAI/main/manifest/recommended.json";
}

public sealed class ProvidersOptions
{
    /// <summary>Master switch for Ollama / OpenAI-compatible / Anthropic and any other remote provider.</summary>
    public bool RemoteEnabled { get; set; } = true;

    /// <summary>Provider ids that are registered but switched off.</summary>
    public IList<string> Disabled { get; set; } = [];
}

public sealed class TelemetryOptions
{
    /// <summary>Emit OpenTelemetry traces and metrics for generations, retrievals and tool calls.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Include prompt and completion text in traces (off by default: privacy).</summary>
    public bool EnableSensitiveData { get; set; }
}
