using DurableOutbox.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DurableOutbox.Tests;

/// <summary>
/// The kit against a context with a naming convention (section 9.3): the convention also applies to the types
/// the library reads its claim into, so without their fixed column names no message is ever claimed.
/// </summary>
public sealed class NamingConventionContractTests(PostgresFixture postgres) : ContractTests<SnakeCaseDbContext>(postgres)
{
    protected override Task RunAsync(Func<OutboxContract<SnakeCaseDbContext>, Task> guarantee) =>
        guarantee(new OutboxContract<SnakeCaseDbContext>(services =>
            services.AddDbContext<SnakeCaseDbContext>(options =>
                options.UseNpgsql(ConnectionString).UseSnakeCaseNamingConvention())));
}
