using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DurableOutbox.Tests;

// Section 9.3: the library's statements do not depend on the service's naming convention. The claim is proved by
// NamingConventionContractTests; these are the other statements whose rows are read into a type.
public sealed class NamingConventionTests(PostgresFixture postgres) : OutboxTestBase(postgres)
{
    [Fact]
    public async Task The_start_up_check_the_gauges_and_the_operator_list_read_their_rows_under_a_naming_convention()
    {
        await AddAsync(new OutboxMessage(Kind, "parked"));
        var (parked, token) = await ClaimOneAsync(maxAttempts: 1);
        Assert.Equal(FailOutcome.Parked, await FailAsync(parked.Id, token, "down", maxAttempts: 1));
        await AddAsync(new OutboxMessage(Kind, "due"));

        await using var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(Time)
            .AddDbContext<SnakeCaseDbContext>(options => options.UseNpgsql(ConnectionString).UseSnakeCaseNamingConvention())
            .AddDurableOutbox<SnakeCaseDbContext>()
            .AddHealthChecks().AddDurableOutbox<SnakeCaseDbContext>(TimeSpan.FromMinutes(5)).Services
            .BuildServiceProvider();
        using var metrics = new MetricsCapture(services.GetRequiredService<OutboxMetrics>().Meter);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SnakeCaseDbContext>();

        // The start-up check (9.11).
        var sql = scope.ServiceProvider.GetRequiredService<OutboxSqlProvider<SnakeCaseDbContext>>().Get(context);
        Assert.Empty(await OutboxSchema.FindDifferencesAsync(context, sql, Ct));

        // The gauges (9.11). A sample that cannot be read is logged, not thrown, so the figures are the proof.
        await services.GetRequiredService<OutboxSampler<SnakeCaseDbContext>>().SampleAsync(Ct);
        metrics.Observe();
        Assert.Equal([1.0], metrics.Values("durable_outbox.messages.pending", ("kind", Kind)));
        Assert.Equal([1.0], metrics.Values("durable_outbox.messages.parked", ("kind", Kind)));

        // The operator list (9.10).
        var listed = Assert.Single(await scope.ServiceProvider.GetRequiredService<IOutboxAdmin>()
            .ListAsync(new OutboxQuery(OutboxMessageStatus.Parked), Ct));
        Assert.Equal(("parked", "down"), (listed.RecordRef, listed.LastError));
    }
}
