namespace Ingot.Internal.Json;

/// <summary>
/// Recovers a JSON payload from free-form model output: strips markdown code fences and
/// extracts the first balanced object or array. String-aware bracket matching only — we do not
/// attempt to "fix" invalid JSON here. Anything this recovers still goes through the real
/// parser; anything it can't recover becomes a NoPayload failure and enters the repair loop.
/// Repair beats heuristics: heuristic fixes hide model misbehavior from the eval data.
/// </summary>
internal static class LenientJson
{
    public static string? TryExtract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var span = text.AsSpan().Trim();

        // Fast path: the whole response is (probably) the payload.
        if (span.Length > 1 && (span[0] == '{' || span[0] == '['))
        {
            var balanced = ExtractBalanced(span, 0);
            if (balanced is not null) return balanced;
        }

        // Fenced block: ```json ... ``` or bare ``` ... ```
        var fenceStart = span.IndexOf("```".AsSpan());
        if (fenceStart >= 0)
        {
            var afterFence = span[(fenceStart + 3)..];
            var newline = afterFence.IndexOf('\n');           // skip the language tag line
            if (newline >= 0) afterFence = afterFence[(newline + 1)..];
            var fenceEnd = afterFence.IndexOf("```".AsSpan());
            if (fenceEnd > 0)
            {
                var inner = afterFence[..fenceEnd].Trim();
                if (inner.Length > 0 && (inner[0] == '{' || inner[0] == '['))
                {
                    var balanced = ExtractBalanced(inner, 0);
                    if (balanced is not null) return balanced;
                }
            }
        }

        // Prose-wrapped: find the first opening bracket and try from there.
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] is '{' or '[')
            {
                return ExtractBalanced(span, i);
            }
        }

        return null;
    }

    private static string? ExtractBalanced(ReadOnlySpan<char> span, int start)
    {
        var open = span[start];
        var close = open == '{' ? '}' : ']';
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < span.Length; i++)
        {
            var c = span[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == open) depth++;
            else if (c == close && --depth == 0) return span[start..(i + 1)].ToString();
        }

        return null; // unbalanced — likely a truncated response; let the repair loop handle it
    }
}
