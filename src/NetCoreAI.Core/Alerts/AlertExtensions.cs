using Microsoft.Extensions.DependencyInjection;
using NetCoreAI.Alerts;

namespace NetCoreAI;

/// <summary>Sending NetCoreAI's alerts somewhere you will see them.</summary>
public static class AlertExtensions
{
    /// <summary>
    /// Sends every alert to a callback — your mailer, your incident tool, a chat channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A lambda rather than an <c>IEmailSender</c> because that interface lives in ASP.NET Core Identity,
    /// and taking a dependency on Identity to get at one interface would put it in every host that
    /// references NetCoreAI. A host that has a mailer already wires it here:
    /// </para>
    /// <code>
    /// builder.Services.AddNetCoreAI()
    ///     .AddAlertSink((alert, ct) => email.SendAsync("ops@example.com", alert.Title, alert.Detail ?? "", ct));
    /// </code>
    /// </remarks>
    public static NetCoreAIBuilder AddAlertSink(this NetCoreAIBuilder builder, Func<Alert, CancellationToken, Task> send)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(send);

        builder.Services.AddSingleton<IAlertSink>(new DelegateAlertSink(send));
        return builder;
    }

    /// <summary>Sends every alert to your own sink type, resolved from the container.</summary>
    public static NetCoreAIBuilder AddAlertSink<TSink>(this NetCoreAIBuilder builder)
        where TSink : class, IAlertSink
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IAlertSink, TSink>();
        return builder;
    }
}
