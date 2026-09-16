using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;

namespace NetCoreAI.Integration.Tests;

/// <summary>
/// A helper the dashboard script calls from one page must not be declared inside another page's block.
/// </summary>
/// <remarks>
/// Reported as "renderCitations is not defined" when an agent with a knowledge base was asked a question.
/// The function was declared inside <c>if (chatForm) { ... }</c>, which never runs on the Agents page, so
/// the agent's citations event threw. It threw inside the stream loop's try, whose catch replaces the
/// bubble with the error text — so the answer was destroyed too, and the agent looked completely broken
/// rather than merely missing its sources.
/// <para>
/// No C# test could have caught that, because nothing executes this file. This checks the shape instead:
/// every function declared below module scope is only called from the page block it lives in. It is
/// written against the whole file rather than the three functions that happened to break, so the next one
/// is caught as well.
/// </para>
/// </remarks>
public sealed class DashboardScriptScopeTests : IAsyncLifetime
{
    private WebApplication _app = default!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddNetCoreAI(o =>
        {
            o.DataDirectory = Path.Combine(Path.GetTempPath(), "netcoreai-tests", Guid.NewGuid().ToString("N"));
            o.Dashboard.AllowAnonymous = true;
        });

        _app = builder.Build();
        _app.MapNetCoreAI();
        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    // The shipped asset, not a file on disk: what the browser runs is what should be checked.
    private Task<string> ScriptAsync() =>
        _app.GetTestClient().GetStringAsync("/netcoreai/_content/app.js", TestContext.Current.CancellationToken);

    // Page blocks are separated by "// ---------- name ----------" banners.
    private static readonly Regex Banner = new(@"^\s*// -{4,}\s*(.+?)\s*-{4,}\s*$", RegexOptions.Compiled);

    // Module scope is two spaces, inside the file's wrapper. Anything deeper sits in some block.
    private static readonly Regex Declaration = new(@"^(\s*)function\s+([A-Za-z_$][\w$]*)\s*\(", RegexOptions.Compiled);

    [Fact]
    public async Task No_function_is_called_from_outside_the_block_that_declares_it()
    {
        var lines = (await ScriptAsync()).Split('\n');
        var sections = Sections(lines);

        var offenders = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var declaration = Declaration.Match(lines[i]);
            if (!declaration.Success || declaration.Groups[1].Value.Length <= 2)
            {
                continue;
            }

            var name = declaration.Groups[2].Value;
            var home = SectionOf(sections, i);
            var call = new Regex(@"(?<![\w$.])" + Regex.Escape(name) + @"\s*\(");

            for (var j = 0; j < lines.Length; j++)
            {
                if (j != i && call.IsMatch(lines[j]) && SectionOf(sections, j) != home)
                {
                    offenders.Add($"{name} is declared inside [{home}] at line {i + 1} but called from [{SectionOf(sections, j)}] at line {j + 1}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A helper shared between pages must be declared at module scope, beside renderSteps, or it is "
            + "undefined on every page but its own:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public async Task The_renderers_an_agent_run_needs_are_reachable_from_the_agents_page()
    {
        var lines = (await ScriptAsync()).Split('\n');

        // The three the agent stream handler calls. Named explicitly as well, because the general check
        // above would pass if somebody deleted the call sites instead of fixing the scope.
        foreach (var name in new[] { "renderCitations", "renderPassages", "renderSteps" })
        {
            var declaration = Array.Find(lines, l => Declaration.Match(l) is { Success: true } m && m.Groups[2].Value == name);

            Assert.True(declaration is not null, $"{name} is not declared at all");
            Assert.Equal(2, Declaration.Match(declaration!).Groups[1].Value.Length);
        }
    }

    private static List<(string Name, int Start, int End)> Sections(string[] lines)
    {
        var sections = new List<(string, int, int)>();
        var current = "prelude";
        var start = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var banner = Banner.Match(lines[i]);
            if (banner.Success)
            {
                sections.Add((current, start, i));
                current = banner.Groups[1].Value;
                start = i;
            }
        }

        sections.Add((current, start, lines.Length));
        return sections;
    }

    private static string SectionOf(List<(string Name, int Start, int End)> sections, int line) =>
        sections.Find(s => line >= s.Start && line < s.End).Name ?? "?";
}
