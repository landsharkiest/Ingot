using System.Text.Json;
using Xunit;

namespace Ingot.Tests;

public sealed class ExtractionOptionsTests
{
    [Fact]
    public void SerializerOptions_AreIsolatedBetweenExtractionOptionsInstances()
    {
        var first = new ExtractionOptions();
        var second = new ExtractionOptions();

        first.SerializerOptions.PropertyNameCaseInsensitive = false;
        first.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;

        Assert.True(second.SerializerOptions.PropertyNameCaseInsensitive);
        Assert.Equal(JsonNamingPolicy.CamelCase, second.SerializerOptions.PropertyNamingPolicy);
        Assert.NotSame(first.SerializerOptions, second.SerializerOptions);
    }
}
