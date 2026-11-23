using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DurableOutbox;

/// <summary>
/// Reports a backlog from the last sample: <c>Degraded</c> when a kind's oldest due message has waited longer
/// than the limit, or when the last sample failed. Never <c>Unhealthy</c>: restarting does not fix a backlog.
/// </summary>
internal sealed class OutboxHealthCheck<TContext>(OutboxSampler<TContext> sampler, TimeSpan degradedAfter) : IHealthCheck
    where TContext : DbContext
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var sample = sampler.Latest;
        if (sample is null)
        {
            return Task.FromResult(HealthCheckResult.Healthy("The outbox has not been sampled yet."));
        }

        if (sample.Failed)
        {
            return Task.FromResult(HealthCheckResult.Degraded("The outbox could not be sampled: the database may be unreachable."));
        }

        var late = sample.Kinds.Where(kind => kind.OldestDueAge > degradedAfter).Select(kind => kind.Kind).ToArray();
        return Task.FromResult(late.Length == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded(
                $"Outbox messages have waited longer than {degradedAfter} after becoming due, in kind {string.Join(", ", late)}."));
    }
}
