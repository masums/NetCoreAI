using Microsoft.AspNetCore.DataProtection;

namespace NetCoreAI.Security;

/// <summary>Encrypts secrets with ASP.NET Core Data Protection under a NetCoreAI-specific purpose.</summary>
internal sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    public const string Purpose = "NetCoreAI.Secrets.v1";
    private const string Prefix = "dp:";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return Prefix + _protector.Protect(plaintext);
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);
        // Values without the prefix were written before protection existed (or by hand); pass them through.
        return protectedValue.StartsWith(Prefix, StringComparison.Ordinal)
            ? _protector.Unprotect(protectedValue[Prefix.Length..])
            : protectedValue;
    }
}
