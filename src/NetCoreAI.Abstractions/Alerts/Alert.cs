namespace NetCoreAI.Alerts;

/// <summary>How much somebody should care.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<AlertSeverity>))]
public enum AlertSeverity
{
    /// <summary>Worth knowing. Nothing is broken yet.</summary>
    Info,

    /// <summary>Heading somewhere bad. Acting now is cheaper than acting later.</summary>
    Warning,

    /// <summary>Something is not working.</summary>
    Error,
}

/// <summary>Something a host should be told about.</summary>
public sealed record Alert
{
    /// <summary>
    /// What this is about, stable across repeats — <c>disk.low</c>, <c>model.load-failed:phi-4</c>.
    /// </summary>
    /// <remarks>
    /// The same key means the same condition, which is how a condition that lasts a week produces one
    /// alert a day rather than one every minute. An alert that arrives every minute is an alert nobody
    /// reads, which is the same as no alerting at all.
    /// </remarks>
    public required string Key { get; init; }

    public AlertSeverity Severity { get; init; } = AlertSeverity.Warning;

    /// <summary>One line, readable in a notification without opening anything.</summary>
    public required string Title { get; init; }

    /// <summary>What to do about it, when there is something to do.</summary>
    public string? Detail { get; init; }

    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Somewhere an alert goes.</summary>
/// <remarks>
/// Implement this to send alerts wherever your host already sends things — your mailer, your incident
/// tool, a chat channel. Every registered sink gets every alert; one that throws is logged and does not
/// stop the others, because the point of having several is that one being down is survivable.
/// </remarks>
public interface IAlertSink
{
    Task SendAsync(Alert alert, CancellationToken cancellationToken = default);
}
