using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ingot.Tests;

public sealed class DependencyInjectionTests
{
    private sealed record Invoice(string VendorName, decimal Total);

    private const string ValidJson = """{"vendorName":"Acme Corp","total":450.00}""";

    [Fact]
    public async Task AddIngotExtraction_ResolvesIExtractor_AndExtracts()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new FakeChatClient("openai", ValidJson));
        services.AddIngotExtraction();

        var extractor = services.BuildServiceProvider().GetRequiredService<IExtractor>();
        var invoice = await extractor.ExtractAsync<Invoice>("Extract the invoice.");

        Assert.Equal("Acme Corp", invoice.VendorName);
        Assert.Equal(450.00m, invoice.Total);
    }

    [Fact]
    public void AddIngotExtraction_AppliesConfiguredOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new FakeChatClient("openai", ValidJson));
        services.AddIngotExtraction(options => options.Retry = RetryPolicy.None);

        var options = services.BuildServiceProvider().GetRequiredService<ExtractionOptions>();

        Assert.Equal(1, options.Retry.MaxAttempts);
    }

    [Fact]
    public void AddIngotExtraction_AutoWiresRegisteredLoggerFactory()
    {
        var factory = new StubLoggerFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new FakeChatClient("openai", ValidJson));
        services.AddSingleton<ILoggerFactory>(factory);
        services.AddIngotExtraction();

        var options = services.BuildServiceProvider().GetRequiredService<ExtractionOptions>();

        Assert.Same(factory, options.Diagnostics.LoggerFactory);
    }

    [Fact]
    public void AddIngotExtraction_DoesNotOverrideExplicitLoggerFactory()
    {
        var registered = new StubLoggerFactory();
        var explicitFactory = new StubLoggerFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new FakeChatClient("openai", ValidJson));
        services.AddSingleton<ILoggerFactory>(registered);
        services.AddIngotExtraction(options => options.Diagnostics.LoggerFactory = explicitFactory);

        var options = services.BuildServiceProvider().GetRequiredService<ExtractionOptions>();

        Assert.Same(explicitFactory, options.Diagnostics.LoggerFactory);
    }

    private sealed class StubLoggerFactory : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }
}
