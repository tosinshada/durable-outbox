using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DurableOutbox;

/// <summary>Registers the outbox with a service's dependency injection.</summary>
public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbox for a service whose <typeparamref name="TContext"/> has the outbox table
    /// in its model (<c>modelBuilder.AddDurableOutbox(...)</c>): the writer, the operator API and the
    /// start-up check of the table. Handlers registered in <paramref name="configure"/> are run by workers
    /// that start with the host, unless <see cref="OutboxOptions.RunWorkers"/> is false.
    /// </summary>
    /// <exception cref="ArgumentException">An option is invalid.</exception>
    public static IServiceCollection AddDurableOutbox<TContext>(
        this IServiceCollection services, Action<OutboxOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new OutboxOptions();
        configure?.Invoke(options);
        var settings = OutboxSettings.From(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<OutboxSqlProvider<TContext>>();
        services.TryAddSingleton(new OutboxRegistration<TContext>(settings));
        services.TryAddSingleton<OutboxSchemaGate<TContext>>();
        services.TryAddSingleton(provider => new OutboxMetrics(provider.GetService<IMeterFactory>()));
        services.TryAddScoped<OutboxStore<TContext>>();
        services.TryAddScoped<IOutbox, Outbox<TContext>>();
        services.TryAddScoped<IOutboxAdmin, OutboxAdmin<TContext>>();
        services.AddHostedService(provider => new OutboxSchemaCheck<TContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<OutboxSchemaGate<TContext>>(),
            provider.GetRequiredService<OutboxRegistration<TContext>>(),
            provider.GetRequiredService<TimeProvider>(),
            Loggers(provider)));

        // Registered whatever RunWorkers says, so the test kit can run them on demand.
        foreach (var kind in settings.Kinds)
        {
            services.TryAddScoped(kind.HandlerType);
        }

        if (options.RunWorkers && settings.Kinds.Count > 0)
        {
            services.AddHostedService(provider => new OutboxWorker<TContext>(
                settings,
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<TimeProvider>(),
                Loggers(provider),
                provider.GetRequiredService<OutboxMetrics>(),
                provider.GetRequiredService<OutboxSchemaGate<TContext>>()));
            services.AddOutboxSampler<TContext>();
        }

        return services;
    }

    /// <summary>The gauge sampler, hosted once per <typeparamref name="TContext"/> however many callers add it.</summary>
    internal static void AddOutboxSampler<TContext>(this IServiceCollection services) where TContext : DbContext
    {
        services.TryAddSingleton(provider => new OutboxSampler<TContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<TimeProvider>(),
            Loggers(provider),
            provider.GetRequiredService<OutboxMetrics>()));
        services.AddHostedService(provider => provider.GetRequiredService<OutboxSampler<TContext>>());
    }

    internal static ILoggerFactory Loggers(IServiceProvider provider) =>
        provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
}
