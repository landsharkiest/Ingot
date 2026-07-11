using Ingot.Internal.Json;
using Xunit;

namespace Ingot.Tests;

public sealed class LenientJsonTests
{
    [Fact]
    public void TryExtract_TruncatedEarlyCandidate_ContinuesToCompletePayload()
    {
        var result = LenientJson.TryExtract("false start { never closed\ncorrected: {\"value\":42}");

        Assert.Equal("{\"value\":42}", result);
    }

    [Fact]
    public void TryExtract_MismatchedEarlyCandidate_ContinuesToCompletePayload()
    {
        var result = LenientJson.TryExtract("bad: {\"items\":[1,2} then good: [1,{\"ok\":true}]");

        Assert.Equal("[1,{\"ok\":true}]", result);
    }

    [Fact]
    public void TryExtract_MixedNestedObjectsAndArrays_ReturnsWholePayload()
    {
        const string payload = "{\"items\":[{\"values\":[1,2]},[3,{\"deep\":[]}]]}";

        Assert.Equal(payload, LenientJson.TryExtract($"Result: {payload} done"));
    }

    [Fact]
    public void TryExtract_DelimitersInsideEscapedString_DoesNotAffectNesting()
    {
        const string payload = "{\"text\":\"braces } ] and quote \\\" and slash \\\\\",\"ok\":true}";

        Assert.Equal(payload, LenientJson.TryExtract($"```json\n{payload}\n```"));
    }

    [Fact]
    public void TryExtract_OnlyMalformedCandidates_ReturnsNull()
    {
        Assert.Null(LenientJson.TryExtract("first {\"x\":[1,2} second [3,4}"));
    }
}
