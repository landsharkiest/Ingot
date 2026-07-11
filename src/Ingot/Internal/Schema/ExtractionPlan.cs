using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Ingot.Internal.Schema;

/// <summary>
/// Everything a strategy needs to shape a request for a target type: the strict-mode-safe
/// JSON schema and stable names. Cached per (type, serializer options) — schema generation
/// walks the type graph and must never run per-request on a hot path.
/// </summary>
internal sealed class ExtractionPlan
{
    private static readonly ConcurrentDictionary<(Type, JsonSerializerOptions), ExtractionPlan> Cache = new();

    private ExtractionPlan(Type targetType, JsonElement schema, string schemaName)
    {
        TargetType = targetType;
        Schema = schema;
        SchemaName = schemaName;
    }

    public Type TargetType { get; }

    /// <summary>Strict-mode-compliant schema (see <see cref="StrictSchemaTransformer"/>).</summary>
    public JsonElement Schema { get; }

    /// <summary>Provider-safe identifier ("invoice", "line_item_batch") derived from the CLR name.</summary>
    public string SchemaName { get; }

    public string SchemaJson => Schema.GetRawText();

    public static ExtractionPlan Create<T>(ExtractionOptions options) =>
        Cache.GetOrAdd((typeof(T), options.SerializerOptions), static key =>
        {
            var (type, serializerOptions) = key;

            // AIJsonUtilities is the platform's schema exporter; we post-process rather than
            // reimplement. If the platform exporter's shape shifts (it has), the transformer
            // is the single seam that absorbs it.
            var raw = AIJsonUtilities.CreateJsonSchema(
                type,
                serializerOptions: serializerOptions);

            var strict = StrictSchemaTransformer.Transform(raw);
            return new ExtractionPlan(type, strict, ToSnakeCase(type.Name));
        });

    private static string ToSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 8);
        foreach (var (c, i) in name.Select(static (c, i) => (c, i)))
        {
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
