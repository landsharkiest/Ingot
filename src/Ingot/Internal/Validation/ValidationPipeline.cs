using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Ingot.Internal.Validation;

/// <summary>
/// Ordered validation stages; the first failing stage short-circuits into the repair loop.
/// Failures are phrased for two audiences at once — the developer reading logs and the model
/// reading the repair prompt — so messages always state the expectation, not just the rejection.
///
///   Stage 0  Parse       System.Text.Json bind (includes all transport conversions: STJ
///                        already speaks ISO-8601 for DateOnly/DateTime/TimeSpan, RFC for
///                        Uri/Guid — a malformed value throws here with a path).
///   Stage 1  Annotations DataAnnotations, applied *recursively* (the BCL Validator only checks
///                        the top object — a classic footgun we own on behalf of users).
///   Stage 2  Semantic    user ISemanticValidator&lt;T&gt; instances (async, may hit databases).
/// </summary>
internal static class ValidationPipeline
{
    public static async ValueTask<(T? Value, IReadOnlyList<ValidationFailure> Failures)> RunAsync<T>(
        string payload,
        ExtractionOptions options,
        CancellationToken ct)
    {
        // Stage 0 — parse/bind.
        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(payload, options.SerializerOptions);
        }
        catch (JsonException ex)
        {
            var path = string.IsNullOrEmpty(ex.Path) ? "$" : ex.Path;
            return (default, [new ValidationFailure(path, TrimParserNoise(ex.Message), FailureCategory.Parse)]);
        }

        if (value is null)
        {
            return (default, [new ValidationFailure(
                "$", "The payload deserialized to null; a complete object is required.",
                FailureCategory.Parse)]);
        }

        // Stage 1 — DataAnnotations, recursive.
        if (options.Validation.UseDataAnnotations)
        {
            var annotationFailures = RecursiveAnnotationValidator.Validate(value, options.SerializerOptions);
            if (annotationFailures.Count > 0) return (value, annotationFailures);
        }

        // Stage 2 — semantic. All validators run (a model fixing three problems in one repair
        // pass beats three sequential repair passes); results are concatenated.
        var semanticFailures = new List<ValidationFailure>();
        foreach (var validator in options.Validation.SemanticValidators)
        {
            if (validator is ISemanticValidator<T> typed)
            {
                semanticFailures.AddRange(await typed.ValidateAsync(value, ct).ConfigureAwait(false));
            }
        }

        return semanticFailures.Count > 0 ? (value, semanticFailures) : (value, []);
    }

    /// <summary>STJ messages embed "Path: … | LineNumber: …" noise that wastes repair tokens
    /// and duplicates the path we already report structurally.</summary>
    private static string TrimParserNoise(string message)
    {
        var idx = message.IndexOf(" Path:", StringComparison.Ordinal);
        return idx > 0 ? message[..idx].TrimEnd() : message;
    }
}

/// <summary>
/// Depth-first DataAnnotations validation over an object graph, tracking JSON paths and
/// guarding against cycles. Scope is deliberate: public readable properties, collections,
/// and nested POCOs; framework/BCL types are treated as leaves.
/// </summary>
internal static class RecursiveAnnotationValidator
{
    public static IReadOnlyList<ValidationFailure> Validate(
        object root,
        JsonSerializerOptions serializerOptions)
    {
        var failures = new List<ValidationFailure>();
        Visit(root, "$", failures, new HashSet<object>(ReferenceEqualityComparer.Instance),
            serializerOptions, depth: 0);
        return failures;
    }

    private const int MaxDepth = 32; // matches STJ's default; a graph deeper than this didn't parse anyway

    private static void Visit(object? instance, string path, List<ValidationFailure> failures,
        HashSet<object> seen, JsonSerializerOptions serializerOptions, int depth)
    {
        if (instance is null || depth > MaxDepth) return;

        var type = instance.GetType();
        if (IsLeaf(type)) return;
        if (!type.IsValueType && !seen.Add(instance)) return; // cycle

        if (instance is IEnumerable enumerable and not string)
        {
            var i = 0;
            foreach (var item in enumerable)
            {
                Visit(item, $"{path}[{i++}]", failures, seen, serializerOptions, depth + 1);
            }
            return;
        }

        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);

        foreach (var result in results)
        {
            var member = result.MemberNames.FirstOrDefault();
            var memberPath = member is null ? path : $"{path}.{JsonNameOf(type, member, serializerOptions)}";
            failures.Add(new ValidationFailure(
                memberPath,
                result.ErrorMessage ?? "Value is invalid.",
                FailureCategory.Annotations));
        }

        foreach (var property in type.GetProperties())
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
            if (IsLeaf(property.PropertyType)) continue;

            object? child;
            try { child = property.GetValue(instance); }
            catch { continue; } // a throwing getter is not an extraction failure

            Visit(child, $"{path}.{JsonNameOf(type, property.Name, serializerOptions)}", failures,
                seen, serializerOptions, depth + 1);
        }
    }

    private static bool IsLeaf(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum
            || type == typeof(string) || type == typeof(decimal)
            || type == typeof(DateTime) || type == typeof(DateTimeOffset)
            || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(TimeSpan)
            || type == typeof(Guid) || type == typeof(Uri);
    }

    /// <summary>Reported paths must match the JSON the model produced, not CLR casing —
    /// the model can't act on <c>$.Total</c> when it wrote <c>"total"</c>.</summary>
    private static string JsonNameOf(
        Type type,
        string clrName,
        JsonSerializerOptions serializerOptions)
    {
        var property = type.GetProperty(clrName);
        var attr = property?.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), inherit: true)
            .OfType<System.Text.Json.Serialization.JsonPropertyNameAttribute>()
            .FirstOrDefault();
        if (attr is not null) return attr.Name;

        return serializerOptions.PropertyNamingPolicy?.ConvertName(clrName) ?? clrName;
    }
}
