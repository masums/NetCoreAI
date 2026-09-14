using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetCoreAI.Tools;

/// <summary>
/// The JSON schema a model is shown for a tool.
/// </summary>
/// <remarks>
/// Built from <see cref="ToolDefinition.ModelParameters"/> alone. This is the enforcement point for the
/// rule that the model cannot set a locked parameter: a parameter bound from a claim is not described here,
/// so there is no property for the model to fill in, and the binder ignores anything extra it sends anyway.
/// Two independent defences, because one of them being wrong is a privilege escalation rather than a bug.
/// </remarks>
internal static class ToolSchema
{
    public static JsonElement For(ToolDefinition tool)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var parameter in tool.ModelParameters)
        {
            var property = new JsonObject { ["type"] = parameter.Type };
            if (parameter.Description is { Length: > 0 } description)
            {
                property["description"] = description;
            }

            if (parameter.Enum is { Count: > 0 } values)
            {
                property["enum"] = new JsonArray([.. values.Select(v => (JsonNode)JsonValue.Create(v)!)]);
            }

            if (parameter.Type == "array")
            {
                // A schema saying only "array" is rejected by some providers and guessed at by others.
                property["items"] = new JsonObject { ["type"] = "string" };
            }

            properties[parameter.Name] = property;
            if (parameter.Required)
            {
                required.Add(parameter.Name);
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        // Named for the model's benefit: providers show this alongside the parameters when deciding a call.
        schema["additionalProperties"] = false;
        return JsonSerializer.Deserialize<JsonElement>(schema.ToJsonString());
    }
}
