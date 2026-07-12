using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Ingot;
using Microsoft.Extensions.AI;
using Xunit;

namespace Ingot.ProviderFixtures;

/// <summary>
/// Ring 2: replays recorded provider responses through the real engine. Closes the coverage gap on
/// the ToolCall and JsonMode strategies (unexercised by the ring-1 fake) and proves cross-provider
/// repair, all without a network.
/// </summary>
public sealed class ProviderReplayTests
{
    private sealed record Invoice(
        string VendorName,
        DateOnly IssuedOn,
        [property: Range(0, 1_000_000)] decimal Total);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static IEnumerable<object[]> AllFixtures() =>
        ProviderFixture.LoadAll().Select(f => new object[] { f.Name });

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public async Task Replay_Fixture_ExtractsExpectedInvoice(string name)
    {
        var fixture = ProviderFixture.LoadAll().Single(f => f.Name == name);
        var expected = fixture.Expected.Deserialize<Invoice>(Web)!;

        var client = new RecordedChatClient(fixture.Provider, fixture.Turns);
        var actual = await client.ExtractAsync<Invoice>("Extract the invoice.");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Anthropic_Fixture_GoesThroughForcedToolCall()
    {
        var fixture = ProviderFixture.LoadAll().Single(f => f.Name == "anthropic-invoice-toolcall");
        var client = new RecordedChatClient(fixture.Provider, fixture.Turns);

        await client.ExtractAsync<Invoice>("Extract the invoice.");

        // Selecting Anthropic must have prepared a forced, schema-named tool — the ToolCall path.
        var options = client.Requests[0].Options;
        Assert.NotNull(options?.Tools);
        Assert.Contains(options!.Tools!, t => t.Name.StartsWith("emit_", StringComparison.Ordinal));
        Assert.IsType<RequiredChatToolMode>(options.ToolMode);
    }

    [Fact]
    public async Task Anthropic_RepairFixture_RecoversAfterRangeFailure()
    {
        var fixture = ProviderFixture.LoadAll().Single(f => f.Name == "anthropic-invoice-repair");
        var client = new RecordedChatClient(fixture.Provider, fixture.Turns);

        var result = await client.TryExtractAsync<Invoice>("Extract the invoice.");

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Attempts.Count);
        Assert.Equal(FailureCategory.Annotations, result.Attempts[0].Failures[0].Category);
        Assert.Equal(1200.50m, result.Value.Total);
    }

    [Fact]
    public async Task ExplicitJsonMode_OnAnyProvider_ExtractsFromResponseText()
    {
        // Force JsonMode regardless of provider metadata to cover the strategy directly.
        var turns = new[]
        {
            new FixtureTurn { Text = "{\"vendorName\":\"Wonka\",\"issuedOn\":\"2026-01-09\",\"total\":12.34}" },
        };
        var client = new RecordedChatClient("openai", turns);

        var invoice = await client.ExtractAsync<Invoice>(
            "Extract the invoice.", new ExtractionOptions { Mode = ExtractionMode.JsonMode });

        Assert.Equal("Wonka", invoice.VendorName);
        Assert.IsType<ChatResponseFormatJson>(client.Requests[0].Options!.ResponseFormat);
    }

    [Fact]
    public async Task ExplicitToolCall_ReadsArgumentsBack()
    {
        var turns = new[]
        {
            new FixtureTurn
            {
                ToolArguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    "{\"vendorName\":\"Stark\",\"issuedOn\":\"2026-03-03\",\"total\":42.00}"),
            },
        };
        var client = new RecordedChatClient("ollama", turns);

        var invoice = await client.ExtractAsync<Invoice>(
            "Extract the invoice.", new ExtractionOptions { Mode = ExtractionMode.ToolCall });

        Assert.Equal("Stark", invoice.VendorName);
        Assert.Equal(42.00m, invoice.Total);
    }
}
