using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DurableOutbox.Tests;

// Section 9.11: the sampled gauges, and the health check that reads them.
public sealed class HealthTests(PostgresFixture postgres) : WorkerTestBase(postgres)
{
    private static readonly TimeSpan DegradedAfter = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task The_gauges_report_the_backlog_and_the_health_check_degrades_when_it_waits_too_long()
    {
        await using var services = Build();
        using var metrics = new MetricsCapture(services.GetRequiredService<OutboxMetrics>().Meter);
        var sampler = services.GetRequiredService<OutboxSampler<TestDbContext>>();

        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(services)).Status);

        await AddAsync(new OutboxMessage(Kind, "parked"));
        var parked = await ClaimOneAsync();
        Assert.Equal(FailOutcome.Parked, await FailAsync(parked.Message.Id, parked.Token, "down", maxAttempts: 1));
        await AddAsync(new OutboxMessage(Kind, "leased") { Priority = 1 });
        Assert.Equal("leased", (await ClaimOneAsync()).Message.RecordRef);
        Time.Advance(TimeSpan.FromSeconds(1));
        await AddAsync(new OutboxMessage(Kind, "due-1"), new OutboxMessage(Kind, "due-2"));

        await sampler.SampleAsync(Ct);
        metrics.Observe();
        Assert.Equal([3.0], metrics.Values("durable_outbox.messages.pending", ("kind", Kind)));
        Assert.Equal([1.0], metrics.Values("durable_outbox.messages.parked", ("kind", Kind)));
        Assert.Equal([0.0], metrics.Values("durable_outbox.oldest_due.age", ("kind", Kind)));
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(services)).Status);

        // The two unclaimed messages became due when they were added; the leased one only when its lease ends.
        Time.Advance(TimeSpan.FromMinutes(6));
        await sampler.SampleAsync(Ct);
        metrics.Clear();
        metrics.Observe();
        Assert.Equal([360.0], metrics.Values("durable_outbox.oldest_due.age", ("kind", Kind)));

        var report = await CheckAsync(services);
        Assert.Equal(HealthStatus.Degraded, report.Status);
        Assert.Contains(Kind, report.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sample_that_fails_degrades_the_health_check_and_never_makes_it_unhealthy()
    {
        await using var services = Build();
        var sampler = services.GetRequiredService<OutboxSampler<TestDbContext>>();
        await sampler.SampleAsync(Ct);

        await SetDatabaseReachableAsync(false);
        await sampler.SampleAsync(Ct);

        Assert.Equal(HealthStatus.Degraded, (await CheckAsync(services)).Status);
        Assert.Contains(Logs.Entries, entry => entry.Contains("could not be sampled", StringComparison.Ordinal));
    }

    /// <summary>A container as a service would register it: the outbox with no workers, and its health check.</summary>
    private ServiceProvider Build() => new ServiceCollection()
        .AddSingleton<TimeProvider>(Time)
        .AddLogging(logging => logging.AddProvider(Logs))
        .AddDbContext<TestDbContext>(options => options.UseNpgsql(ConnectionString))
        .AddDurableOutbox<TestDbContext>()
        .AddHealthChecks().AddDurableOutbox<TestDbContext>(DegradedAfter).Services
        .BuildServiceProvider();

    private static async Task<HealthReportEntry> CheckAsync(IServiceProvider services) =>
        Assert.Single((await services.GetRequiredService<HealthCheckService>().CheckHealthAsync(Ct)).Entries).Value;
}
