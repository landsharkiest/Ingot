using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ingot.Internal.Schema;

/// <summary>
/// Rewrites an exported JSON schema so the *strictest* consumer (OpenAI structured outputs,
/// <c>strict: true</c>) accepts it, without losing information the model needs.
///
/// This is the fix for the ecosystem's documented pain: strict mode rejects the <c>format</c>
/// keyword, which is exactly how exporters describe <c>DateTime</c>, <c>DateOnly</c>,
/// <c>TimeSpan</c>, <c>Uri</c>, <c>Guid</c>… The insight that keeps this class small:
/// System.Text.Json already round-trips all of those types as ISO-8601 / RFC strings, so we do
/// NOT need a converter layer — we need the schema to keep *telling the model* the expected
/// string shape after we delete the keyword strict mode chokes on. So:
///
///   1. Every <c>format</c> keyword is removed and folded into <c>description</c>
///      ("ISO 8601 date (e.g. 2026-07-10)"), preserving model guidance.
///   2. Every object gets <c>additionalProperties: false</c> and a <c>required</c> array listing
///      ALL properties (strict mode's rule). Optionality is expressed as <c>["T","null"]</c>
///      unions, matching how OpenAI models optional fields.
///   3. Unsupported assertion keywords (minLength, pattern, minimum…) are folded into
///      <c>description</c> too — the model still sees the constraint, and DataAnnotations
///      enforce it for real after parsing. Belt and braces: guidance in the schema,
///      enforcement in the pipeline.
///
/// Malformed dates that slip through anyway surface as parse-stage failures and enter the
/// repair loop — a deliberate single error path rather than a special case.
/// </summary>
internal static class StrictSchemaTransformer
{
    private static readonly Dictionary<string, string> FormatHints = new(StringComparer.Ordinal)
    {
        ["date-time"] = "ISO 8601 date-time (e.g. 2026-07-10T14:30:00Z)",
        ["date"] = "ISO 8601 date (e.g. 2026-07-10)",
        ["time"] = "ISO 8601 time (e.g. 14:30:00)",
        ["duration"] = "ISO 8601 duration or .NET TimeSpan (e.g. PT1H30M or 01:30:00)",
        ["uri"] = "absolute URI (e.g. https://example.com/path)",
        ["uuid"] = "UUID (e.g. 123e4567-e89b-12d3-a456-426614174000)",
        ["email"] = "email address",
    };

    // Assertion keywords strict mode rejects but which carry model-useful intent.
    private static readonly string[] FoldableAssertions =
        ["pattern",
            "minLength",
            "maxLength",
            "minimum",
            "maximum",
            "exclusiveMinimum",
            "exclusiveMaximum",
            "minItems",
            "maxItems",
            "multipleOf"];

    public static JsonElement Transform(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText())
            ?? throw new InvalidOperationException("Schema exporter produced an empty document.");
        Visit(node);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void Visit(JsonNode node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null) Visit(item);
            }
            return;
        }

        if (node is not JsonObject obj) return;

        FoldFormatIntoDescription(obj);
        FoldAssertionsIntoDescription(obj);

        if (obj["properties"] is JsonObject properties)
        {
            // Strict mode: additionalProperties must be false, required must list everything.
            obj["additionalProperties"] = false;
            obj["required"] = new JsonArray([.. properties.Select(p => (JsonNode)p.Key)]);
        }

        // Schema maps contain arbitrary member names, so the map itself is not a schema and
        // cannot be passed to Visit directly. Visit each mapped schema instead.
        VisitSchemaMap(obj["properties"]);
        VisitSchemaMap(obj["$defs"]);
        VisitSchemaMap(obj["definitions"]);

        // Single-schema locations.
        VisitSchema(obj["items"]);
        VisitSchema(obj["additionalProperties"]);

        // Composition locations contain arrays of schemas.
        VisitSchemaArray(obj["anyOf"]);
        VisitSchemaArray(obj["allOf"]);
        VisitSchemaArray(obj["oneOf"]);
    }

    private static void VisitSchemaMap(JsonNode? node)
    {
        if (node is not JsonObject map) return;

        foreach (var (_, schema) in map)
        {
            VisitSchema(schema);
        }
    }

    private static void VisitSchemaArray(JsonNode? node)
    {
        if (node is not JsonArray schemas) return;

        foreach (var schema in schemas)
        {
            VisitSchema(schema);
        }
    }

    private static void VisitSchema(JsonNode? schema)
    {
        // JSON Schema also permits boolean schemas. They need no transformation.
        if (schema is JsonObject or JsonArray) Visit(schema);
    }

    private static void FoldFormatIntoDescription(JsonObject obj)
    {
        if (obj["format"] is not JsonValue formatValue) return;

        var format = formatValue.GetValue<string>();
        obj.Remove("format");

        var hint = FormatHints.TryGetValue(format, out var known) ? known : $"format: {format}";
        AppendToDescription(obj, hint);
    }

    private static void FoldAssertionsIntoDescription(JsonObject obj)
    {
        foreach (var keyword in FoldableAssertions)
        {
            if (obj[keyword] is not JsonNode value) continue;
            obj.Remove(keyword);
            AppendToDescription(obj, $"{keyword}: {value.ToJsonString()}");
        }
    }

    private static void AppendToDescription(JsonObject obj, string hint)
    {
        var existing = obj["description"]?.GetValue<string>();
        obj["description"] = string.IsNullOrEmpty(existing) ? hint : $"{existing} ({hint})";
    }
}
