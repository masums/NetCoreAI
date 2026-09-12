using Microsoft.Extensions.Configuration;

namespace NetCoreAI.Security;

/// <summary>Resolves the plaintext secret of a connection: environment/configuration override first, then the protected value.</summary>
public interface ISecretResolver
{
    /// <summary>
    /// Looks up <c>NetCoreAI:Connections:{Name}:Secret</c> (spaces replaced by underscores) and
    /// <c>NetCoreAI:Connections:{Id}:Secret</c> in configuration (so <c>NETCOREAI__CONNECTIONS__OPENAI_PROD__SECRET</c> works
    /// in containers), then falls back to decrypting <see cref="ProviderConnection.ProtectedSecret"/>.
    /// </summary>
    string? Resolve(ProviderConnection connection);
}

internal sealed class SecretResolver(ISecretProtector protector, IConfiguration configuration) : ISecretResolver
{
    public string? Resolve(ProviderConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var fromEnv = configuration[$"NetCoreAI:Connections:{connection.Name.Replace(' ', '_')}:Secret"]
            ?? configuration[$"NetCoreAI:Connections:{connection.Id}:Secret"];
        if (!string.IsNullOrEmpty(fromEnv))
        {
            return fromEnv;
        }

        return connection.ProtectedSecret is null ? null : protector.Unprotect(connection.ProtectedSecret);
    }
}
