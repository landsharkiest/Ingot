using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Ingot.Internal.Schema;

/// <summary>
/// Everything a strategy needs to shape a request for a target type: the strict-mode-safe
/// JSON schema and stable names. Cached per (type, serializer fingerprint) — schema generation
/// walks the type graph and must never run per-request on a hot path.
/// </summary>
internal sealed class ExtractionPlan
{
    // Keyed on a *fingerprint* of the schema-affecting serializer settings, not the
    // JsonSerializerOptions instance. ExtractionOptions hands out a fresh (mutable) options
    // instance per call by design, so an instance-keyed cache never hits — every request
    // would regenerate the schema. Two equivalent default-web option instances share a
    // fingerprint and therefore a plan.
    private static readonly ConcurrentDictionary<SchemaCacheKey, ExtractionPlan> Cache = new();

    private readonly record struct SchemaCacheKey(Type Type, string SerializerFingerprint);

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
        Cache.GetOrAdd(
            new SchemaCacheKey(typeof(T), Fingerprint(options.SerializerOptions)),
            // TArg overload so the factory stays static (no per-call closure) while still
            // receiving the live options — the fingerprint in the key can't rebuild the schema.
            static (key, serializerOptions) =>
            {
                // AIJsonUtilities is the platform's schema exporter; we post-process rather than
                // reimplement. If the platform exporter's shape shifts (it has), the transformer
                // is the single seam that absorbs it.
                var raw = AIJsonUtilities.CreateJsonSchema(
                    key.Type,
                    serializerOptions: serializerOptions);

                var strict = StrictSchemaTransformer.Transform(raw);
                return new ExtractionPlan(key.Type, strict, ToSnakeCase(key.Type.Name));
            },
            options.SerializerOptions);

    /// <summary>
    /// A stable string over the serializer settings that change the emitted schema (property
    /// naming, number handling, ignore rules, resolver, converters). Settings that only affect
    /// reading (e.g. case-insensitivity) are deliberately excluded so they don't fragment the
    /// cache. Equivalent option instances produce equal fingerprints.
    /// </summary>
    private static string Fingerprint(JsonSerializerOptions o)
    {
        var sb = new StringBuilder(128);
        sb.Append(o.PropertyNamingPolicy?.GetType().FullName ?? "-").Append('\n');
        sb.Append(o.DictionaryKeyPolicy?.GetType().FullName ?? "-").Append('\n');
        sb.Append((int)o.NumberHandling).Append('\n');
        sb.Append((int)o.DefaultIgnoreCondition).Append('\n');
        sb.Append(o.TypeInfoResolver?.GetType().FullName ?? "-").Append('\n');
        foreach (var converter in o.Converters)
        {
            sb.Append(converter.GetType().FullName).Append(',');
        }
        return sb.ToString();
    }

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
