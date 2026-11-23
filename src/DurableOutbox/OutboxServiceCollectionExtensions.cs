using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DurableOutbox;

public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbox for a service whose <typeparamref name="TContext"/> has the outbox table
    /// in its model (<c>modelBuilder.AddDurableOutbox(...)</c>). Handlers registered in
    /// <paramref name="configure"/> are run by workers that start with the host, unless
    /// <see cref="OutboxOptions.RunWorkers"/> is false.
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
        services.TryAddScoped<OutboxStore<TContext>>();
        services.TryAddScoped<IOutbox, Outbox<TContext>>();

        if (options.RunWorkers && settings.Kinds.Count > 0)
        {
            foreach (var kind in settings.Kinds)
            {
                services.TryAddScoped(kind.HandlerType);
            }

            services.AddHostedService(provider => new OutboxWorker<TContext>(
                settings,
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance));
        }

        return services;
    }
}
