using System.Text;
using System.Text.Json;

namespace NetCoreAI.Backends.Gguf;

/// <summary>
/// Converts a JSON Schema into a GBNF grammar so llama.cpp can constrain sampling to valid output.
/// This is how a GGUF model satisfies <c>ChatResponseFormat.ForJsonSchema</c> without validate-and-retry.
/// </summary>
/// <remarks>
/// Supported: object (properties, required, additionalProperties), array (items, minItems, maxItems),
/// string (enum, const, minLength/maxLength as repetition), number, integer, boolean, null, enum, const,
/// anyOf / oneOf, and $ref into $defs or definitions. Anything unsupported degrades to "any JSON value",
/// which still guarantees syntactically valid JSON.
/// </remarks>
public static class JsonSchemaToGbnf
{
    private const int MaxDepth = 24;

    /// <summary>Grammar accepting any JSON value; the fallback when no schema is supplied.</summary>
    public const string AnyJson = """
        root   ::= value
        value  ::= object | array | string | number | boolean | null
        object ::= "{" ws ( string ws ":" ws value ( ws "," ws string ws ":" ws value )* )? ws "}"
        array  ::= "[" ws ( value ( ws "," ws value )* )? ws "]"
        string ::= "\"" char* "\""
        char   ::= [^"\\\x7F\x00-\x1F] | "\\" (["\\bfnrt/] | "u" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F])
        number ::= "-"? ("0" | [1-9] [0-9]*) ("." [0-9]+)? ([eE] [-+]? [0-9]+)?
        boolean ::= "true" | "false"
        null   ::= "null"
        ws     ::= [ \t\n]*
        """;

    /// <summary>Builds a GBNF grammar whose root matches <paramref name="schema"/>.</summary>
    public static string Convert(JsonElement schema)
    {
        var builder = new Builder(schema);
        return builder.Build();
    }

    /// <summary>Builds a grammar from schema JSON text. Returns <see cref="AnyJson"/> when the text is not valid JSON.</summary>
    public static string Convert(string schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return AnyJson;
        }

        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            return Convert(document.RootElement);
        }
        catch (JsonException)
        {
            return AnyJson;
        }
    }

    private sealed class Builder(JsonElement root)
    {
        private readonly Dictionary<string, string> _rules = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _refs = new(StringComparer.Ordinal);
        private readonly List<string> _order = [];
        private int _counter;

        public string Build()
        {
            var rootRule = Visit(root, "root", 0);
            var sb = new StringBuilder();
            // The root production comes first; llama.cpp starts from the rule literally named "root".
            sb.Append("root ::= ").AppendLine(rootRule);
            foreach (var name in _order)
            {
                sb.Append(name).Append(" ::= ").AppendLine(_rules[name]);
            }

            AppendPrimitives(sb);
            return sb.ToString();
        }

        // GBNF primitive definitions. Each is emitted only when something references it.
        private static readonly (string Name, string Rule)[] Primitives =
        [
            ("value", """value ::= object | array | string | number | boolean | null"""),
            ("object", """object ::= "{" ws ( string ws ":" ws value ( ws "," ws string ws ":" ws value )* )? ws "}" """),
            ("array", """array ::= "[" ws ( value ( ws "," ws value )* )? ws "]" """),
            ("string", """string ::= "\"" char* "\"" """),
            ("char", """char ::= [^"\\\x7F\x00-\x1F] | "\\" (["\\bfnrt/] | "u" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F])"""),
            ("number", """number ::= "-"? ("0" | [1-9] [0-9]*) ("." [0-9]+)? ([eE] [-+]? [0-9]+)?"""),
            ("integer", """integer ::= "-"? ("0" | [1-9] [0-9]*)"""),
            ("boolean", """boolean ::= "true" | "false" """),
            ("null", """null ::= "null" """),
            ("ws", """ws ::= [ \t\n]*"""),
        ];

        private static void AppendPrimitives(StringBuilder sb)
        {
            // "value" pulls in object and array, which pull in string, number and the rest, so resolve transitively.
            var needed = new HashSet<string>(StringComparer.Ordinal) { "ws" };
            var text = sb.ToString();
            foreach (var (name, _) in Primitives)
            {
                if (References(text, name))
                {
                    needed.Add(name);
                }
            }

            bool added;
            do
            {
                added = false;
                foreach (var (name, rule) in Primitives.Where(p => needed.Contains(p.Name)))
                {
                    foreach (var (other, _) in Primitives)
                    {
                        if (!needed.Contains(other) && References(rule[(rule.IndexOf("::=", StringComparison.Ordinal) + 3)..], other))
                        {
                            needed.Add(other);
                            added = true;
                        }
                    }
                }
            }
            while (added);

            foreach (var (name, rule) in Primitives.Where(p => needed.Contains(p.Name)))
            {
                sb.AppendLine(rule.TrimEnd());
            }
        }

        /// <summary>True when the text uses <paramref name="name"/> as a rule reference rather than inside a literal.</summary>
        private static bool References(string text, string name)
        {
            var index = 0;
            while ((index = text.IndexOf(name, index, StringComparison.Ordinal)) >= 0)
            {
                var before = index == 0 ? ' ' : text[index - 1];
                var afterIndex = index + name.Length;
                var after = afterIndex >= text.Length ? ' ' : text[afterIndex];
                // A rule reference is delimited by non-identifier characters on both sides, and a definition
                // ("name ::=") does not count as a reference to itself.
                var isIdentifierBoundary = !char.IsLetterOrDigit(before) && before != '-' && !char.IsLetterOrDigit(after) && after != '-';
                var isDefinition = text.AsSpan(afterIndex).TrimStart().StartsWith("::=", StringComparison.Ordinal);
                if (isIdentifierBoundary && !isDefinition)
                {
                    return true;
                }

                index = afterIndex;
            }

            return false;
        }

        private string AddRule(string suggestedName, string body)
        {
            var name = Sanitize(suggestedName);
            if (_rules.ContainsKey(name))
            {
                name = $"{name}-{++_counter}";
            }

            _rules[name] = body;
            _order.Add(name);
            return name;
        }

        /// <summary>Returns a GBNF expression matching the schema; complex parts become named rules.</summary>
        private string Visit(JsonElement schema, string name, int depth)
        {
            if (depth > MaxDepth || schema.ValueKind != JsonValueKind.Object)
            {
                return "value";
            }

            if (schema.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String)
            {
                return ResolveRef(reference.GetString()!, depth);
            }

            if (schema.TryGetProperty("const", out var constant))
            {
                return Literal(constant);
            }

            if (schema.TryGetProperty("enum", out var enumValues) && enumValues.ValueKind == JsonValueKind.Array)
            {
                var options = enumValues.EnumerateArray().Select(Literal).ToList();
                return options.Count == 0 ? "value" : AddRule(name, string.Join(" | ", options));
            }

            foreach (var keyword in new[] { "anyOf", "oneOf" })
            {
                if (schema.TryGetProperty(keyword, out var union) && union.ValueKind == JsonValueKind.Array)
                {
                    var options = union.EnumerateArray().Select((s, i) => Visit(s, $"{name}-{i}", depth + 1)).ToList();
                    return options.Count == 0 ? "value" : AddRule(name, string.Join(" | ", options));
                }
            }

            var type = schema.TryGetProperty("type", out var typeElement) ? TypeOf(typeElement) : null;

            return type switch
            {
                "object" => VisitObject(schema, name, depth),
                "array" => VisitArray(schema, name, depth),
                "string" => VisitString(schema, name),
                "integer" => "integer",
                "number" => "number",
                "boolean" => "boolean",
                "null" => "null",
                // No type given but properties are: treat it as an object, which is what schema authors mean.
                null when schema.TryGetProperty("properties", out _) => VisitObject(schema, name, depth),
                _ => "value",
            };
        }

        private static string? TypeOf(JsonElement type) => type.ValueKind switch
        {
            JsonValueKind.String => type.GetString(),
            // ["string","null"] and similar unions: take the first concrete type.
            JsonValueKind.Array => type.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.String && e.GetString() != "null").GetString(),
            _ => null,
        };

        private string VisitObject(JsonElement schema, string name, int depth)
        {
            if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            {
                return "object";
            }

            var required = new HashSet<string>(StringComparer.Ordinal);
            if (schema.TryGetProperty("required", out var requiredList) && requiredList.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in requiredList.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String))
                {
                    required.Add(r.GetString()!);
                }
            }

            // Required properties first in schema order, then optional ones as trailing optional groups.
            // Models constrained by a grammar emit properties in the order the grammar allows, which is why
            // the order is fixed rather than permuted (permuting explodes the rule count).
            var members = properties.EnumerateObject().ToList();
            var requiredMembers = members.Where(m => required.Contains(m.Name)).ToList();
            var optionalMembers = members.Where(m => !required.Contains(m.Name)).ToList();

            var body = new StringBuilder("\"{\" ws ");
            var first = true;

            foreach (var member in requiredMembers)
            {
                if (!first)
                {
                    body.Append(" \",\" ws ");
                }

                body.Append(PropertyExpression(member, name, depth));
                first = false;
            }

            foreach (var member in optionalMembers)
            {
                var expression = PropertyExpression(member, name, depth);
                body.Append(first ? $" ( {expression}" : $" ( \",\" ws {expression}");
                body.Append(" )?");
                // An optional property that is the first emitted one still must not be followed by a stray comma,
                // so subsequent optionals keep their own leading comma inside their group.
                first = false;
            }

            body.Append(" ws \"}\"");
            return AddRule(name, body.ToString());
        }

        private string PropertyExpression(JsonProperty member, string parentName, int depth)
        {
            var valueRule = Visit(member.Value, $"{parentName}-{member.Name}", depth + 1);
            // The key is a JSON string, so the GBNF literal contains escaped quotes: "\"key\""
            return $"{JsonStringLiteral(member.Name)} ws \":\" ws {valueRule}";
        }

        /// <summary>A GBNF literal matching the JSON string <paramref name="text"/>, quotes included.</summary>
        private static string JsonStringLiteral(string text) => "\"\\\"" + Escape(text) + "\\\"\"";

        private string VisitArray(JsonElement schema, string name, int depth)
        {
            var itemRule = schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object
                ? Visit(items, $"{name}-item", depth + 1)
                : "value";

            var min = schema.TryGetProperty("minItems", out var minElement) && minElement.TryGetInt32(out var m) ? Math.Max(0, m) : 0;
            var max = schema.TryGetProperty("maxItems", out var maxElement) && maxElement.TryGetInt32(out var x) ? x : (int?)null;

            var body = new StringBuilder("\"[\" ws ");
            if (min == 0 && max is null)
            {
                body.Append($"( {itemRule} ( ws \",\" ws {itemRule} )* )?");
            }
            else
            {
                // Emit the mandatory items, then optional ones up to the cap.
                var parts = new List<string>();
                for (var i = 0; i < Math.Max(min, 1); i++)
                {
                    parts.Add(i == 0 ? itemRule : $"ws \",\" ws {itemRule}");
                }

                var tail = max is { } cap
                    ? string.Concat(Enumerable.Range(0, Math.Max(0, cap - Math.Max(min, 1))).Select(_ => $" ( ws \",\" ws {itemRule} )?"))
                    : $" ( ws \",\" ws {itemRule} )*";

                var core = string.Join(' ', parts) + tail;
                body.Append(min == 0 ? $"( {core} )?" : core);
            }

            body.Append(" ws \"]\"");
            return AddRule(name, body.ToString());
        }

        private string VisitString(JsonElement schema, string name)
        {
            // A bounded-length string becomes an explicit repetition so the model cannot ramble.
            var min = schema.TryGetProperty("minLength", out var minElement) && minElement.TryGetInt32(out var m) ? Math.Max(0, m) : 0;
            var max = schema.TryGetProperty("maxLength", out var maxElement) && maxElement.TryGetInt32(out var x) ? x : (int?)null;
            if (min == 0 && max is null)
            {
                return "string";
            }

            const string quote = "\"\\\"\"";   // GBNF literal for a single double-quote character
            var body = new StringBuilder(quote).Append(' ');
            for (var i = 0; i < min; i++)
            {
                body.Append("char ");
            }

            body.Append(max is { } cap
                ? string.Concat(Enumerable.Range(0, Math.Max(0, cap - min)).Select(_ => "char? "))
                : "char* ");
            body.Append(quote);
            return AddRule(name, body.ToString());
        }

        private string ResolveRef(string pointer, int depth)
        {
            if (_refs.TryGetValue(pointer, out var existing))
            {
                return existing;
            }

            var segments = pointer.TrimStart('#', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            foreach (var segment in segments)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(Unescape(segment), out var next))
                {
                    return "value";
                }

                current = next;
            }

            var name = segments.Length > 0 ? segments[^1] : "ref";
            // Reserve the name before recursing so a self-referencing schema terminates.
            var ruleName = Sanitize(name);
            if (_rules.ContainsKey(ruleName))
            {
                ruleName = $"{ruleName}-{++_counter}";
            }

            _refs[pointer] = ruleName;
            _rules[ruleName] = "value";
            _order.Add(ruleName);
            var body = Visit(current, ruleName + "-body", depth + 1);
            // If the body collapsed to a single rule reference, alias it; otherwise inline the expression.
            _rules[ruleName] = body;
            return ruleName;
        }

        private static string Unescape(string segment) => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

        private static string Literal(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => JsonStringLiteral(value.GetString() ?? ""),
            JsonValueKind.Number => $"\"{value.GetRawText()}\"",
            JsonValueKind.True => "\"true\"",
            JsonValueKind.False => "\"false\"",
            JsonValueKind.Null => "\"null\"",
            _ => "value",
        };

        private static string Escape(string text) => text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

        private static string Sanitize(string name)
        {
            var chars = name.Select(c => char.IsLetterOrDigit(c) || c == '-' ? char.ToLowerInvariant(c) : '-').ToArray();
            var cleaned = new string(chars).Trim('-');
            while (cleaned.Contains("--", StringComparison.Ordinal))
            {
                cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);
            }

            if (cleaned.Length == 0 || char.IsDigit(cleaned[0]))
            {
                cleaned = "r" + cleaned;
            }

            // Never collide with the primitive rule names emitted at the end.
            return cleaned is "root" or "value" or "object" or "array" or "string" or "char" or "number" or "integer" or "boolean" or "null" or "ws"
                ? cleaned + "-rule"
                : cleaned;
        }
    }

}
