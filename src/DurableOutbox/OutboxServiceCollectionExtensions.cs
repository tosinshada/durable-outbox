using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DurableOutbox;

public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbox for a service whose <typeparamref name="TContext"/> has the outbox table
    /// in its model (<c>modelBuilder.AddDurableOutbox(...)</c>).
    /// </summary>
    public static IServiceCollection AddDurableOutbox<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<OutboxSqlProvider<TContext>>();
        services.TryAddScoped<OutboxStore<TContext>>();
        services.TryAddScoped<IOutbox, Outbox<TContext>>();

        return services;
    }
}
