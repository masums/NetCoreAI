using NetCoreAI.Tools.BuiltIn;
using Xunit;

namespace NetCoreAI.Core.Tests.Tools;

/// <summary>
/// The tools NetCoreAI ships with, and mostly what they refuse. Two of them reach outside the process, and
/// what they will not do matters more than what they will.
/// </summary>
public class BuiltInToolTests
{
    // ---------- arithmetic ----------

    [Theory]
    [InlineData("2+2", "4")]
    [InlineData("(1200 * 1.2) / 3", "480")]
    [InlineData("10 % 3", "1")]
    [InlineData("2^10", "1024")]
    [InlineData("-5 + 3", "-2")]
    [InlineData("1,200 + 300", "1500")]
    [InlineData("2 ^ 3 ^ 2", "512")]
    public void Arithmetic_is_worked_out(string expression, string expected) =>
        Assert.Equal(expected, CalculatorTool.Calculate(expression));

    [Theory]
    [InlineData("System.Environment.Exit(0)")]
    [InlineData("1 + ")]
    [InlineData("(1 + 2")]
    [InlineData("DROP TABLE orders")]
    [InlineData("")]
    public void Anything_that_is_not_arithmetic_is_refused_rather_than_evaluated(string expression)
    {
        // The input comes from a model, which means it comes from whoever is talking to the model. An
        // expression evaluator that can reach a type system is a way to run code by asking nicely.
        var result = CalculatorTool.Calculate(expression);

        Assert.Contains("not an expression", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dividing_by_zero_says_so_rather_than_returning_infinity()
    {
        // A model will put "∞" in a sentence and mean it.
        Assert.Contains("finite", CalculatorTool.Calculate("1/0"), StringComparison.OrdinalIgnoreCase);
    }

    // ---------- fetching ----------

    [Theory]
    [InlineData("https://docs.example.com/page", new[] { "docs.example.com" }, true)]
    [InlineData("http://docs.example.com/page", new[] { "docs.example.com" }, true)]
    [InlineData("https://DOCS.EXAMPLE.COM/page", new[] { "docs.example.com" }, true)]
    [InlineData("https://other.com/page", new[] { "docs.example.com" }, false)]
    [InlineData("https://docs.example.com/page", new string[0], false)]
    public void Only_an_approved_host_can_be_fetched(string url, string[] allowed, bool expected) =>
        Assert.Equal(expected, FetchTool.Allowed(new Uri(url), allowed));

    [Theory]
    [InlineData("https://evil-docs.example.com/page")]
    [InlineData("https://docs.example.com.evil.net/page")]
    public void A_host_that_merely_resembles_an_approved_one_is_refused(string url)
    {
        // A suffix or prefix match would let either of these through an allow-list naming example.com.
        Assert.False(FetchTool.Allowed(new Uri(url), ["docs.example.com", "example.com"]));
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1:8080/admin")]
    [InlineData("http://10.0.0.5/internal")]
    [InlineData("http://[::1]/admin")]
    public void An_address_literal_is_refused_however_the_allow_list_reads(string url)
    {
        // These are the addresses worth reaching from inside a network: the metadata service that hands
        // out cloud credentials, an admin panel bound to localhost, anything on a private range. An
        // allow-list of names says nothing about what an address currently points at.
        Assert.False(FetchTool.Allowed(new Uri(url), ["169.254.169.254", "127.0.0.1", "10.0.0.5", "::1"]));
    }

    // ---------- reading a database ----------

    private static readonly SqlToolConnection Connection = new("Microsoft.Data.Sqlite", "Data Source=:memory:");

    [Theory]
    [InlineData("SELECT * FROM orders")]
    [InlineData("  select id from orders where total > 10  ")]
    [InlineData("WITH recent AS (SELECT * FROM orders) SELECT * FROM recent")]
    [InlineData("SELECT * FROM orders;")]
    public void A_select_is_allowed(string sql) => Assert.Null(SqlQueryTool.Refuse(sql, Connection));

    [Theory]
    [InlineData("DELETE FROM orders")]
    [InlineData("UPDATE orders SET total = 0")]
    [InlineData("DROP TABLE orders")]
    [InlineData("INSERT INTO orders VALUES (1)")]
    [InlineData("TRUNCATE TABLE orders")]
    [InlineData("EXEC sp_who")]
    [InlineData("GRANT ALL ON orders TO public")]
    public void Anything_that_writes_is_refused(string sql) =>
        Assert.NotNull(SqlQueryTool.Refuse(sql, Connection));

    [Fact]
    public void A_second_statement_smuggled_after_a_select_is_refused()
    {
        // The classic: a check that only reads the start of the string passes this happily.
        var refusal = SqlQueryTool.Refuse("SELECT 1; DELETE FROM orders", Connection);

        Assert.NotNull(refusal);
        Assert.Contains("one statement", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_write_hidden_inside_a_select_is_refused()
    {
        // Not every database allows this, but the ones that do are exactly the ones worth defending.
        Assert.NotNull(SqlQueryTool.Refuse("SELECT * FROM orders WHERE id IN (DELETE FROM audit RETURNING id)", Connection));
    }

    [Fact]
    public void A_table_allow_list_keeps_a_read_only_account_out_of_the_wrong_tables()
    {
        var scoped = Connection with { AllowedTables = ["orders", "customers"] };

        // "Read-only" and "may read the password hashes" are not mutually exclusive.
        Assert.Null(SqlQueryTool.Refuse("SELECT * FROM orders", scoped));
        Assert.NotNull(SqlQueryTool.Refuse("SELECT * FROM users", scoped));
    }

    [Fact]
    public void An_empty_query_asks_for_one_rather_than_running_it()
    {
        Assert.NotNull(SqlQueryTool.Refuse("   ", Connection));
    }

    // ---------- the date ----------

    [Fact]
    public void The_current_date_is_given_in_words_a_model_will_not_misread()
    {
        var now = DateTimeTool.Now();

        Assert.Contains(DateTimeOffset.UtcNow.Year.ToString(System.Globalization.CultureInfo.InvariantCulture), now, StringComparison.Ordinal);
        Assert.Contains("UTC", now, StringComparison.Ordinal);
    }
}
