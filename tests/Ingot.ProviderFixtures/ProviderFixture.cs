using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ingot.ProviderFixtures;

/// <summary>One turn of a recorded exchange: either a tool-call (arguments) or assistant text.</summary>
public sealed class FixtureTurn
{
    /// <summary>Tool-call arguments, for tool-call providers (Anthropic). Mutually exclusive with <see cref="Text"/>.</summary>
    public Dictionary<string, JsonElement>? ToolArguments { get; set; }

    /// <summary>Raw assistant text, for JSON-mode / native-schema / prompted providers.</summary>
    public string? Text { get; set; }
}

/// <summary>
/// A recorded provider exchange plus the object it should extract to. Hand-authored to mirror the
/// response shapes each provider returns for structured output; the format is exactly what real
/// captured responses will drop into once live recording is wired up.
/// </summary>
public sealed class ProviderFixture
{
    public string Name { get; set; } = "";

    /// <summary>Provider identifier, resolved to a strategy (anthropic→ToolCall, ollama→JsonMode, …).</summary>
    public string Provider { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>One response per model round-trip; more than one turn exercises the repair loop.</summary>
    public List<FixtureTurn> Turns { get; set; } = [];

    /// <summary>The invoice the extraction must produce.</summary>
    public JsonElement Expected { get; set; }

    private static readonly JsonSerializerOptions LoadOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads every <c>*.json</c> fixture from the <c>Fixtures</c> directory beside the test assembly.</summary>
    public static IReadOnlyList<ProviderFixture> LoadAll()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var fixtures = new List<ProviderFixture>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(static f => f))
        {
            var fixture = JsonSerializer.Deserialize<ProviderFixture>(File.ReadAllText(file), LoadOptions)
                ?? throw new InvalidOperationException($"Fixture '{file}' deserialized to null.");
            fixtures.Add(fixture);
        }
        return fixtures;
    }
}
