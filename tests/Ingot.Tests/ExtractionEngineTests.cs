using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.AI;
using Xunit;

namespace Ingot.Tests;

public sealed class ExtractionEngineTests
{
    private sealed record Invoice(
        string VendorName,
        DateOnly IssuedOn,
        [property: Range(0, 1_000_000)] decimal Total);

    private const string ValidInvoiceJson =
        """{"vendorName":"Acme Corp","issuedOn":"2026-06-01","total":450.00}""";

    // ---------------------------------------------------------------- happy path

    [Fact]
    public async Task Extract_ValidFirstAttempt_ReturnsTypedValue_WithOneCall()
    {
        var client = new FakeChatClient("openai", ValidInvoiceJson);

        var invoice = await client.ExtractAsync<Invoice>("Extract the invoice.");

        Assert.Equal("Acme Corp", invoice.VendorName);
        Assert.Equal(new DateOnly(2026, 6, 1), invoice.IssuedOn);   // DateOnly transport works
        Assert.Equal(450.00m, invoice.Total);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task Extract_OnOpenAI_UsesNativeSchemaResponseFormat()
    {
        var client = new FakeChatClient("openai", ValidInvoiceJson);

        await client.ExtractAsync<Invoice>("Extract the invoice.");

        var options = client.Requests[0].Options;
        Assert.NotNull(options);
        Assert.IsType<ChatResponseFormatJson>(options.ResponseFormat);
    }

    // ---------------------------------------------------------------- repair loop

    [Fact]
    public async Task Extract_MalformedDate_RepairsOnSecondAttempt()
    {
        var client = new FakeChatClient("openai",
            """{"vendorName":"Acme Corp","issuedOn":"June 1st, 2026","total":450.00}""",
            ValidInvoiceJson);

        var result = await client.TryExtractAsync<Invoice>("Extract the invoice.");

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Attempts.Count);
        Assert.Equal(FailureCategory.Parse, result.Attempts[0].Failures[0].Category);

        // The repair conversation must contain the failed output followed by the failure list.
        var repairRequest = client.Requests[1].Messages;
        Assert.Contains(repairRequest, m => m.Role == ChatRole.Assistant && m.Text!.Contains("June 1st"));
        Assert.Contains(repairRequest, m => m.Role == ChatRole.User && m.Text!.Contains("failed validation"));
    }

    [Fact]
    public async Task Extract_RangeViolation_FeedsAnnotationFailureBackToModel()
    {
        var client = new FakeChatClient("openai",
            """{"vendorName":"Acme Corp","issuedOn":"2026-06-01","total":-450.00}""",
            ValidInvoiceJson);

        var result = await client.TryExtractAsync<Invoice>("Extract the invoice.");

        Assert.True(result.IsSuccess);
        var failure = result.Attempts[0].Failures.Single();
        Assert.Equal(FailureCategory.Annotations, failure.Category);
        Assert.Equal("$.total", failure.Path);   // JSON casing, not CLR casing

        var repairText = client.Requests[1].Messages[^1].Text!;
        Assert.Contains("$.total", repairText);
    }

    [Fact]
    public async Task Extract_SemanticValidatorFailure_TriggersRepair()
    {
        var client = new FakeChatClient("openai", ValidInvoiceJson,
            """{"vendorName":"Globex","issuedOn":"2026-06-01","total":450.00}""");

        var options = new ExtractionOptions();
        options.Validation.SemanticValidators.Add(new VendorMustBeGlobex());

        var result = await client.TryExtractAsync<Invoice>("Extract the invoice.", options);

        Assert.True(result.IsSuccess);
        Assert.Equal("Globex", result.Value.VendorName);
        Assert.Equal(FailureCategory.Semantic, result.Attempts[0].Failures.Single().Category);
    }

    private sealed class VendorMustBeGlobex : ISemanticValidator<Invoice>
    {
        public ValueTask<IReadOnlyList<ValidationFailure>> ValidateAsync(Invoice value, CancellationToken ct)
        {
            IReadOnlyList<ValidationFailure> failures = value.VendorName == "Globex"
                ? []
                : [new ValidationFailure("$.vendorName",
                    $"Vendor '{value.VendorName}' is not an approved vendor; the approved vendor is 'Globex'.",
                    FailureCategory.Semantic)];
            return ValueTask.FromResult(failures);
        }
    }

    // ---------------------------------------------------------------- exhaustion

    [Fact]
    public async Task Extract_AllAttemptsFail_ThrowsWithFullHistory_AndAggregatedUsage()
    {
        var client = new FakeChatClient("openai", "not json", "still not json", "nope");

        var ex = await Assert.ThrowsAsync<ExtractionException>(
            () => client.ExtractAsync<Invoice>("Extract the invoice."));

        Assert.Equal(3, ex.Attempts.Count);
        Assert.Equal(typeof(Invoice), ex.TargetType);
        Assert.Equal(3, client.CallCount);

        var result = await new FakeChatClient("openai", "not json")
            .TryExtractAsync<Invoice>("x", new ExtractionOptions { Retry = RetryPolicy.None });
        Assert.False(result.IsSuccess);
        Assert.Equal(150, result.AggregateUsage.TotalTokenCount);
    }

    // ---------------------------------------------------------------- prompted / lenient path

    [Fact]
    public async Task Extract_UnknownProvider_FallsBackToPrompted_AndStripsFences()
    {
        var fenced = $"Sure! Here is the invoice you asked for:\n```json\n{ValidInvoiceJson}\n```\nLet me know if you need anything else.";
        var client = new FakeChatClient("mystery-llm", fenced);

        var invoice = await client.ExtractAsync<Invoice>("Extract the invoice.");

        Assert.Equal("Acme Corp", invoice.VendorName);
        // Prompted strategy must have injected the schema as a system message.
        Assert.Equal(ChatRole.System, client.Requests[0].Messages[0].Role);
        Assert.Contains("JSON schema", client.Requests[0].Messages[0].Text!);
    }

    [Fact]
    public async Task Extract_CallerChatOptions_AreClonedNotMutated()
    {
        var callerOptions = new ChatOptions { Temperature = 0f };
        var client = new FakeChatClient("openai", ValidInvoiceJson);

        await client.ExtractAsync<Invoice>("Extract.", new ExtractionOptions { ChatOptions = callerOptions });

        Assert.Null(callerOptions.ResponseFormat);                       // caller's instance untouched
        Assert.NotNull(client.Requests[0].Options!.ResponseFormat);      // request got the schema
        Assert.Equal(0f, client.Requests[0].Options!.Temperature);       // pass-through preserved
    }
}
