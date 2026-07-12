using System.Text.Json;
using Ingot.Internal.Schema;
using Xunit;

namespace Ingot.Tests;

public sealed class ExtractionPlanCacheTests
{
    private sealed record Money(string CurrencyCode, decimal TotalAmount);

    [Fact]
    public void Create_EquivalentDefaultOptionsOnDistinctInstances_ReturnsCachedPlan()
    {
        // ExtractionOptions hands out a fresh SerializerOptions per instance; the cache must
        // still hit so the schema is generated once, not per request.
        var first = ExtractionPlan.Create<Money>(new ExtractionOptions());
        var second = ExtractionPlan.Create<Money>(new ExtractionOptions());

        Assert.Same(first, second);
    }

    [Fact]
    public void Create_DifferentNamingPolicy_ReturnsDistinctPlansWithDifferentSchemas()
    {
        var camel = new ExtractionOptions();
        var snake = new ExtractionOptions();
        snake.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;

        var camelPlan = ExtractionPlan.Create<Money>(camel);
        var snakePlan = ExtractionPlan.Create<Money>(snake);

        Assert.NotSame(camelPlan, snakePlan);
        // The naming policy changes emitted property names, so the schemas must differ.
        Assert.NotEqual(camelPlan.SchemaJson, snakePlan.SchemaJson);
    }
}
