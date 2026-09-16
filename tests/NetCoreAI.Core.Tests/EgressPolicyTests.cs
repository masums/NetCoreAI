using NetCoreAI.Hub;
using Xunit;

namespace NetCoreAI.Core.Tests;

/// <summary>
/// The data-residency switch: what may leave the process when a host has said "nothing, except these".
/// </summary>
/// <remarks>
/// One definition of "allowed", because there used to be two — the HTTP handler understood
/// <c>*.example.com</c> and the provider check did not — and a residency policy that means two different
/// things in two places has a hole in it by construction.
/// </remarks>
public class EgressPolicyTests
{
    private static NetworkOptions Offline(params string[] allowed)
    {
        var network = new NetworkOptions { OfflineMode = true };
        foreach (var host in allowed)
        {
            network.AllowedHosts.Add(host);
        }

        return network;
    }

    [Fact]
    public void With_the_switch_off_everything_is_allowed()
    {
        // The default, and it must cost nothing: a host that has not asked for this should not be able to
        // tell the feature exists.
        Assert.True(EgressPolicy.IsAllowed(new Uri("https://api.openai.com/v1"), new NetworkOptions()));
    }

    [Fact]
    public void With_the_switch_on_an_unlisted_host_is_refused()
    {
        Assert.False(EgressPolicy.IsAllowed(new Uri("https://api.openai.com/v1"), Offline("mirror.internal")));
    }

    [Fact]
    public void An_approved_mirror_is_reachable()
    {
        Assert.True(EgressPolicy.IsAllowed(new Uri("https://mirror.internal/models"), Offline("mirror.internal")));
    }

    [Theory]
    [InlineData("http://localhost:11434/api/tags")]
    [InlineData("http://127.0.0.1:8080/v1")]
    [InlineData("http://[::1]/v1")]
    public void This_machine_is_not_somewhere_else(string url)
    {
        // A local Ollama, a sidecar, a loopback mirror: none of them is data leaving anywhere, and
        // refusing them would make the switch unusable exactly where it is most wanted.
        Assert.True(EgressPolicy.IsAllowed(new Uri(url), Offline()));
    }

    [Theory]
    [InlineData("https://mirror.example.com/x", true)]
    [InlineData("https://a.b.example.com/x", true)]
    [InlineData("https://example.com/x", false)]
    [InlineData("https://evil-example.com/x", false)]
    [InlineData("https://notexample.com/x", false)]
    public void A_wildcard_covers_subdomains_and_nothing_that_merely_looks_like_one(string url, bool expected)
    {
        // The usual way an allow-list turns out to allow everything is a plain suffix match, which lets
        // evil-example.com through a list naming example.com.
        Assert.Equal(expected, EgressPolicy.IsAllowed(new Uri(url), Offline("*.example.com")));
    }

    [Fact]
    public void An_empty_allow_list_means_nothing_leaves()
    {
        Assert.False(EgressPolicy.IsAllowed(new Uri("https://anywhere.example"), Offline()));
    }

    [Fact]
    public void An_address_that_cannot_be_parsed_is_refused_rather_than_assumed_harmless()
    {
        Assert.False(EgressPolicy.IsAllowed((Uri?)null, Offline("mirror.internal")));
        Assert.False(EgressPolicy.IsAllowed("not a url", Offline("mirror.internal")));
    }

    [Fact]
    public void An_unparseable_address_is_only_refused_when_the_switch_is_on()
    {
        // Otherwise turning residency off would start refusing things it never refused before, which is
        // the wrong direction for a setting whose off position means "behave as you always did".
        Assert.True(EgressPolicy.IsAllowed("not a url", new NetworkOptions()));
    }

    [Fact]
    public void The_refusal_names_the_host_and_says_what_to_do_about_it()
    {
        var message = EgressPolicy.Refusal("api.openai.com");

        Assert.Contains("api.openai.com", message, StringComparison.Ordinal);
        Assert.Contains("AllowedHosts", message, StringComparison.Ordinal);
    }
}
