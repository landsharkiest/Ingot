using Ingot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Ingot's extraction service into a <see cref="IServiceCollection"/>.</summary>
public static class IngotServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IExtractor"/> (and the <see cref="ExtractionOptions"/> it uses), resolving
    /// the <see cref="IChatClient"/> from DI. If an <see cref="ILoggerFactory"/> is registered and the
    /// caller did not set one, it is wired into <see cref="DiagnosticsOptions.LoggerFactory"/> so
    /// extraction logging works with zero extra configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration for the shared <see cref="ExtractionOptions"/>.</param>
    /// <returns>The same <paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddIngotExtraction(
        this IServiceCollection services,
        Action<ExtractionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(serviceProvider =>
        {
            var options = new ExtractionOptions();
            configure?.Invoke(options);
            options.Diagnostics.LoggerFactory ??= serviceProvider.GetService<ILoggerFactory>();
            return options;
        });

        // Transient so it composes with whatever lifetime the IChatClient uses (no captive dependency).
        services.TryAddTransient<IExtractor>(serviceProvider => new ChatClientExtractor(
            serviceProvider.GetRequiredService<IChatClient>(),
            serviceProvider.GetRequiredService<ExtractionOptions>()));

        return services;
    }
}
