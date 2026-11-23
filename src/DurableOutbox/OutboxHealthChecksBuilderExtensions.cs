using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DurableOutbox;

/// <summary>Adds the outbox health check to a service's health checks.</summary>
public static class OutboxHealthChecksBuilderExtensions
{
    /// <summary>
    /// Adds a health check for the outbox of <typeparamref name="TContext"/>, which must be registered with
    /// <c>services.AddDurableOutbox&lt;TContext&gt;(…)</c>. It reports <c>Degraded</c> when a message has
    /// waited longer than <paramref name="degradedAfter"/> after becoming due, or when the outbox could not
    /// be sampled, and never <c>Unhealthy</c>. It reads a sample taken every 15 seconds and runs no query.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="degradedAfter"/> is not positive.</exception>
    public static IHealthChecksBuilder AddDurableOutbox<TContext>(this IHealthChecksBuilder builder, TimeSpan degradedAfter)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(degradedAfter, TimeSpan.Zero);

        builder.Services.AddOutboxSampler<TContext>();
        return builder.Add(new HealthCheckRegistration(
            $"DurableOutbox ({typeof(TContext).Name})",
            provider => new OutboxHealthCheck<TContext>(provider.GetRequiredService<OutboxSampler<TContext>>(), degradedAfter),
            HealthStatus.Degraded,
            tags: null));
    }
}
