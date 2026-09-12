using System.Text.Json;
using NetCoreAI.Backends.Gguf;
using Xunit;

namespace NetCoreAI.Core.Tests.Gguf;

public class JsonSchemaToGbnfTests
{
    private static string Convert(string schema) => JsonSchemaToGbnf.Convert(schema);

    [Fact]
    public void Object_schema_pins_required_properties_and_their_types()
    {
        var gbnf = Convert("""
            { "type": "object",
              "properties": { "name": { "type": "string" }, "age": { "type": "integer" } },
              "required": ["name", "age"] }
            """);

        Assert.StartsWith("root ::=", gbnf);
        Assert.Contains("\"\\\"name\\\"\"", gbnf, StringComparison.Ordinal);
        Assert.Contains("\"\\\"age\\\"\"", gbnf, StringComparison.Ordinal);
        Assert.Contains("integer ::=", gbnf, StringComparison.Ordinal);
        Assert.Contains("string ::=", gbnf, StringComparison.Ordinal);
        Assert.Contains("ws ::=", gbnf, StringComparison.Ordinal);
    }

    [Fact]
    public void Optional_properties_become_optional_groups()
    {
        var gbnf = Convert("""
            { "type": "object",
              "properties": { "id": { "type": "integer" }, "note": { "type": "string" } },
              "required": ["id"] }
            """);

        // The optional property is wrapped in ( ... )? and carries its own comma.
        Assert.Contains(")?", gbnf, StringComparison.Ordinal);
        Assert.Contains("\",\"", gbnf, StringComparison.Ordinal);
    }

    [Fact]
    public void Enum_becomes_a_choice_of_literals()
    {
        var gbnf = Convert("""{ "type": "string", "enum": ["low", "high"] }""");

        Assert.Contains("\"\\\"low\\\"\"", gbnf, StringComparison.Ordinal);
        Assert.Contains("\"\\\"high\\\"\"", gbnf, StringComparison.Ordinal);
        Assert.Contains("|", gbnf, StringComparison.Ordinal);
    }

    [Fact]
    public void Const_becomes_a_single_literal()
    {
        Assert.Contains("\"\\\"fixed\\\"\"", Convert("""{ "const": "fixed" }"""), StringComparison.Ordinal);
        Assert.Contains("\"true\"", Convert("""{ "const": true }"""), StringComparison.Ordinal);
    }

    [Fact]
    public void Array_of_objects_produces_an_item_rule()
    {
        var gbnf = Convert("""
            { "type": "array",
              "items": { "type": "object", "properties": { "sku": { "type": "string" } }, "required": ["sku"] } }
            """);

        Assert.Contains("\"[\"", gbnf, StringComparison.Ordinal);
        Assert.Contains("\"]\"", gbnf, StringComparison.Ordinal);
        Assert.Contains("\"\\\"sku\\\"\"", gbnf, StringComparison.Ordinal);
    }

    [Fact]
    public void Bounded_array_emits_the_minimum_items_explicitly()
    {
        var gbnf = Convert("""{ "type": "array", "items": { "type": "integer" }, "minItems": 2, "maxItems": 3 }""");

        // Two mandatory items plus one optional: three references to the item rule in the array body.
        var arrayRule = gbnf.Split('\n').First(l => l.Contains("\"[\"", StringComparison.Ordinal));
        Assert.Equal(3, arrayRule.Split("integer").Length - 1);
    }

    [Fact]
    public void Bounded_string_expands_into_character_repetitions()
    {
        var gbnf = Convert("""{ "type": "string", "minLength": 2, "maxLength": 4 }""");
        var rule = gbnf.Split('\n').First(l => l.Contains("::=", StringComparison.Ordinal) && !l.StartsWith("char ::=", StringComparison.Ordinal) && l.Contains("char", StringComparison.Ordinal));

        // Two mandatory characters, then two optional ones to reach the maximum of four.
        Assert.Equal(2, CountOccurrences(rule, "char "));
        Assert.Equal(2, CountOccurrences(rule, "char? "));
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    [Fact]
    public void AnyOf_becomes_alternatives()
    {
        var gbnf = Convert("""{ "anyOf": [ { "type": "string" }, { "type": "integer" } ] }""");
        Assert.Contains("string | integer", gbnf, StringComparison.Ordinal);
    }

    [Fact]
    public void Ref_into_defs_is_resolved_into_a_named_rule()
    {
        var gbnf = Convert("""
            { "type": "object",
              "properties": { "address": { "$ref": "#/$defs/address" } },
              "required": ["address"],
              "$defs": { "address": { "type": "object", "properties": { "city": { "type": "string" } }, "required": ["city"] } } }
            """);

        Assert.Contains("address ::=", gbnf, StringComparison.Ordinal);
        Assert.Contains("\"\\\"city\\\"\"", gbnf, StringComparison.Ordinal);
    }

    [Fact]
    public void Self_referencing_schema_terminates()
    {
        var gbnf = Convert("""
            { "$ref": "#/$defs/node",
              "$defs": { "node": { "type": "object",
                "properties": { "child": { "$ref": "#/$defs/node" } } } } }
            """);

        Assert.Contains("node ::=", gbnf, StringComparison.Ordinal);
        Assert.NotEmpty(gbnf);
    }

    [Fact]
    public void Unknown_or_invalid_schemas_fall_back_to_any_json()
    {
        Assert.Equal(JsonSchemaToGbnf.AnyJson, JsonSchemaToGbnf.Convert("not json at all"));
        Assert.Equal(JsonSchemaToGbnf.AnyJson, JsonSchemaToGbnf.Convert(""));
        // A schema with no recognisable type still constrains output to valid JSON.
        Assert.Contains("value", Convert("""{ "description": "anything" }"""), StringComparison.Ordinal);
    }

    [Fact]
    public void Rule_names_never_collide_with_primitives()
    {
        var gbnf = Convert("""
            { "type": "object",
              "properties": { "string": { "type": "string" }, "number": { "type": "number" } },
              "required": ["string", "number"] }
            """);

        // The generated rules for properties named "string"/"number" must be renamed, so each
        // primitive is still defined exactly once.
        Assert.Equal(1, gbnf.Split('\n').Count(l => l.StartsWith("string ::=", StringComparison.Ordinal)));
        Assert.Equal(1, gbnf.Split('\n').Count(l => l.StartsWith("number ::=", StringComparison.Ordinal)));
    }

    [Fact]
    public void Property_names_with_quotes_are_escaped()
    {
        var gbnf = Convert("""{ "type": "object", "properties": { "a\"b": { "type": "string" } }, "required": ["a\"b"] }""");
        Assert.Contains("\\\\\"", gbnf, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_rule_reference_resolves_to_a_definition()
    {
        var gbnf = Convert("""
            { "type": "object",
              "properties": {
                "name": { "type": "string" },
                "tags": { "type": "array", "items": { "type": "string" } },
                "score": { "type": "number" },
                "active": { "type": "boolean" },
                "meta": { "type": "object", "properties": { "k": { "type": "integer" } } } },
              "required": ["name", "tags", "score", "active", "meta"] }
            """);

        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in gbnf.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = line.IndexOf("::=", StringComparison.Ordinal);
            if (index > 0)
            {
                defined.Add(line[..index].Trim());
            }
        }

        // Collect identifiers used outside of quoted literals and character classes.
        foreach (var line in gbnf.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = line.IndexOf("::=", StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            var body = StripLiterals(line[(index + 3)..]);
            foreach (var token in body.Split([' ', '(', ')', '|', '?', '*', '+'], StringSplitOptions.RemoveEmptyEntries))
            {
                var name = token.Trim();
                if (name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '-'))
                {
                    Assert.True(defined.Contains(name), $"rule '{name}' is referenced but never defined:\n{gbnf}");
                }
            }
        }
    }

    /// <summary>Removes "..." literals and [...] character classes so only rule references remain.</summary>
    private static string StripLiterals(string text)
    {
        var sb = new System.Text.StringBuilder();
        var inString = false;
        var inClass = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\' && i + 1 < text.Length)
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (inClass)
            {
                if (c == '\\' && i + 1 < text.Length)
                {
                    i++;
                }
                else if (c == ']')
                {
                    inClass = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '[':
                    inClass = true;
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    [Fact]
    public void Handles_a_realistic_extraction_schema()
    {
        var schema = JsonSerializer.Deserialize<JsonElement>("""
            {
              "type": "object",
              "properties": {
                "invoiceNumber": { "type": "string" },
                "total": { "type": "number" },
                "currency": { "type": "string", "enum": ["USD", "EUR", "BDT"] },
                "lines": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": { "description": { "type": "string" }, "amount": { "type": "number" } },
                    "required": ["description", "amount"]
                  }
                }
              },
              "required": ["invoiceNumber", "total", "currency"]
            }
            """);

        var gbnf = JsonSchemaToGbnf.Convert(schema);

        Assert.Contains("\"\\\"invoiceNumber\\\"\"", gbnf, StringComparison.Ordinal);
        Assert.Contains("\"\\\"USD\\\"\"", gbnf, StringComparison.Ordinal);
        Assert.Contains("number ::=", gbnf, StringComparison.Ordinal);
        Assert.StartsWith("root ::=", gbnf);
    }
}
