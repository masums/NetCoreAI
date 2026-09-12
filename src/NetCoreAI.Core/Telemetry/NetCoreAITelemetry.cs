using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NetCoreAI.Telemetry;

/// <summary>Shared ActivitySource / Meter. Hosts subscribe with OpenTelemetry using these names.</summary>
public static class NetCoreAITelemetry
{
    public const string SourceName = "NetCoreAI";

    public static readonly ActivitySource ActivitySource = new(SourceName, ThisAssembly.Version);

    public static readonly Meter Meter = new(SourceName, ThisAssembly.Version);

    public static readonly Counter<long> Requests = Meter.CreateCounter<long>("netcoreai.requests", description: "Chat/embedding requests by model and outcome.");

    public static readonly Counter<long> Errors = Meter.CreateCounter<long>("netcoreai.errors", description: "Failed chat/embedding requests by model.");

    public static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>("netcoreai.request.duration", unit: "ms");

    public static readonly Counter<long> InputTokens = Meter.CreateCounter<long>("netcoreai.tokens.input");

    public static readonly Counter<long> OutputTokens = Meter.CreateCounter<long>("netcoreai.tokens.output");

    public static readonly UpDownCounter<long> LoadedModels = Meter.CreateUpDownCounter<long>("netcoreai.models.loaded");

    public static readonly UpDownCounter<long> ActiveGenerations = Meter.CreateUpDownCounter<long>("netcoreai.generations.active");
}

internal static class ThisAssembly
{
    public static readonly string Version =
        typeof(ThisAssembly).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
