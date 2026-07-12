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

        // Fast path: the whole response is (probably) the payload. If that opening bracket is
        // truncated or mismatched, keep scanning: models sometimes emit a false start followed
        // by a complete corrected payload in the same response.
        var balanced = TryExtractFirstBalanced(span);
        if (balanced is not null && span[0] is '{' or '[') return balanced;

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
                balanced = TryExtractFirstBalanced(inner);
                if (balanced is not null) return balanced;
            }
        }

        // Prose-wrapped: find the first opening bracket and try from there.
        return TryExtractFirstBalanced(span);
    }

    private static string? TryExtractFirstBalanced(ReadOnlySpan<char> span)
    {
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] is '{' or '[')
            {
                var candidate = ExtractBalanced(span, i);
                if (candidate is not null) return candidate;

                // ExtractBalanced rejected this candidate (mismatched/truncated/unbalanced).
                // If this is the very first character of the span (i == 0), skip past the
                // candidate's likely extent to avoid extracting balanced nested content, which
                // could be stale partial data. For mid-span candidates, continue char-by-char
                // to handle "model restarts" after prose.
                if (i == 0)
                {
                    var depth = 1;
                    var inString = false;
                    var escaped = false;

                    for (var j = i + 1; j < span.Length; j++)
                    {
                        var c = span[j];

                        if (inString)
                        {
                            if (escaped) escaped = false;
                            else if (c == '\\') escaped = true;
                            else if (c == '"') inString = false;
                            continue;
                        }

                        if (c == '"')
                        {
                            inString = true;
                        }
                        else if (c is '{' or '[')
                        {
                            depth++;
                        }
                        else if (c is '}' or ']')
                        {
                            depth--;
                            if (depth <= 0)
                            {
                                // Found a potential closer. Resume searching from after this position.
                                i = j;
                                break;
                            }
                        }
                    }

                    // If we never found a closer (depth > 0), the candidate extends to end of
                    // span. No more candidates to try, return null.
                    if (depth > 0)
                    {
                        return null;
                    }
                }
                // For i > 0, just continue to the next character (original behavior)
            }
        }

        return null;
    }

    private static string? ExtractBalanced(ReadOnlySpan<char> span, int start)
    {
        // JSON nesting can freely alternate objects and arrays. Track the expected closing
        // delimiter at each level so a mismatched candidate is rejected rather than accidentally
        // closed by a later bracket of the outer type.
        Span<char> closers = stackalloc char[64];
        char[]? rented = null;
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

            if (c == '"')
            {
                inString = true;
            }
            else if (c is '{' or '[')
            {
                if (depth == closers.Length)
                {
                    var expanded = new char[closers.Length * 2];
                    closers.CopyTo(expanded);
                    rented = expanded;
                    closers = rented;
                }

                closers[depth++] = c == '{' ? '}' : ']';
            }
            else if (c is '}' or ']')
            {
                if (depth == 0 || closers[depth - 1] != c) return null;
                if (--depth == 0) return span[start..(i + 1)].ToString();
            }
        }

        return null; // unbalanced — likely a truncated response; let the repair loop handle it
    }
}
