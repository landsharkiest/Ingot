using System.Text.Json;
using Microsoft.Extensions.AI;
using Ingot.Internal.Json;
using Ingot.Internal.Schema;

namespace Ingot.Internal.Strategies;

/// <summary>
/// A strategy owns two things: shaping the request so the model emits schema-conforming JSON,
/// and pulling the JSON payload back out of the response. Strategies are stateless singletons.
/// The interface is deliberately tiny so community provider strategies stay trivial to write —
/// it will be made public (with an options hook for registration) once the shape survives
/// contact with a third provider.
/// </summary>
internal interface IExtractionStrategy
{
    void Prepare(List<ChatMessage> conversation, ChatOptions chatOptions, ExtractionPlan plan);

    /// <summary>Returns the JSON payload text, or null when the response contains nothing extractable.</summary>
    string? TryExtractPayload(ChatResponse response);
}

internal static class StrategyResolver
{
    public static IExtractionStrategy Resolve(IChatClient client, ExtractionMode mode)
    {
        if (mode != ExtractionMode.Auto) return ForMode(mode);

        // Capability resolution from metadata. Provider identifiers below follow the
        // conventions Microsoft.Extensions.AI adapters currently emit; the table is small on
        // purpose — Prompted is a universally-correct fallback, so unknown providers degrade
        // safely rather than failing loudly. Users can pin a mode per call to override.
        var provider = client.GetService(typeof(ChatClientMetadata)) is ChatClientMetadata meta
            ? meta.ProviderName?.ToLowerInvariant()
            : null;

        return provider switch
        {
            "openai" or "azure.ai.openai" or "azureopenai" => NativeSchemaStrategy.Instance,
            "anthropic" => ToolCallStrategy.Instance,
            "ollama" => JsonModeStrategy.Instance,
            _ => PromptedStrategy.Instance,
        };
    }

    private static IExtractionStrategy ForMode(ExtractionMode mode) => mode switch
    {
        ExtractionMode.NativeSchema => NativeSchemaStrategy.Instance,
        ExtractionMode.ToolCall => ToolCallStrategy.Instance,
        ExtractionMode.JsonMode => JsonModeStrategy.Instance,
        ExtractionMode.Prompted => PromptedStrategy.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}

/// <summary>OpenAI-style structured outputs: the schema rides in ResponseFormat with strict
/// enforcement. The provider guarantees syntactic conformance; our pipeline still runs — schema
/// validity is not business validity.</summary>
internal sealed class NativeSchemaStrategy : IExtractionStrategy
{
    public static NativeSchemaStrategy Instance { get; } = new();

    public void Prepare(List<ChatMessage> conversation, ChatOptions chatOptions, ExtractionPlan plan)
    {
        chatOptions.ResponseFormat = ChatResponseFormat.ForJsonSchema(
            plan.Schema, plan.SchemaName, description: null);
    }

    public string? TryExtractPayload(ChatResponse response) =>
        string.IsNullOrWhiteSpace(response.Text) ? null : response.Text;
}

/// <summary>
/// Schema-as-a-required-tool. On Anthropic models a forced tool call is the most reliable
/// structured path; the "tool" is never invoked — we only read its arguments back.
/// </summary>
internal sealed class ToolCallStrategy : IExtractionStrategy
{
    public static ToolCallStrategy Instance { get; } = new();

    public void Prepare(List<ChatMessage> conversation, ChatOptions chatOptions, ExtractionPlan plan)
    {
        var tool = new DeclaredExtractionTool(plan);
        chatOptions.Tools ??= [];
        chatOptions.Tools.Add(tool);
        chatOptions.ToolMode = ChatToolMode.RequireSpecific(tool.Name);
    }

    public string? TryExtractPayload(ChatResponse response)
    {
        var call = response.Messages
            .SelectMany(static m => m.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault();

        return call is null ? null : JsonSerializer.Serialize(call.Arguments);
    }

    /// <summary>
    /// A schema-only tool declaration. Invocation is a contract violation by design: nothing in
    /// the pipeline registers this with function-invocation middleware, and if a future
    /// integrator does, we want a loud failure, not a silent no-op.
    /// </summary>
    private sealed class DeclaredExtractionTool(ExtractionPlan plan) : AIFunction
    {
        public override string Name => $"emit_{plan.SchemaName}";

        public override string Description =>
            $"Record the extracted {plan.SchemaName}. Call exactly once with the complete data.";

        public override JsonElement JsonSchema => plan.Schema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException(
                "Ingot extraction tools are declarations only and must never be invoked.");
    }
}

/// <summary>Provider JSON mode plus schema-in-system-prompt. Valid JSON is guaranteed by the
/// provider; schema conformance is ours to validate and repair.</summary>
internal sealed class JsonModeStrategy : IExtractionStrategy
{
    public static JsonModeStrategy Instance { get; } = new();

    public void Prepare(List<ChatMessage> conversation, ChatOptions chatOptions, ExtractionPlan plan)
    {
        chatOptions.ResponseFormat = ChatResponseFormat.Json;
        conversation.Insert(0, new ChatMessage(ChatRole.System, SchemaPrompt.Build(plan)));
    }

    public string? TryExtractPayload(ChatResponse response) =>
        string.IsNullOrWhiteSpace(response.Text) ? null : response.Text;
}

/// <summary>Last resort for any model: schema in the prompt, lenient payload recovery
/// (code-fence stripping, first-balanced-JSON extraction) on the way back.</summary>
internal sealed class PromptedStrategy : IExtractionStrategy
{
    public static PromptedStrategy Instance { get; } = new();

    public void Prepare(List<ChatMessage> conversation, ChatOptions chatOptions, ExtractionPlan plan)
    {
        conversation.Insert(0, new ChatMessage(ChatRole.System, SchemaPrompt.Build(plan)));
    }

    public string? TryExtractPayload(ChatResponse response) =>
        LenientJson.TryExtract(response.Text);
}

internal static class SchemaPrompt
{
    public static string Build(ExtractionPlan plan) =>
        $"""
        You are a precise data extraction engine.
        Respond with a single JSON object conforming exactly to this JSON schema:

        {plan.SchemaJson}

        Rules: output ONLY the JSON object — no prose, no markdown, no code fences.
        Omit nothing; use null only where the schema permits it.
        """;
}
