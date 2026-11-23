namespace DurableOutbox.Tests;

/// <summary>The kit against <c>svc.outbox</c>, the table the library's other tests use.</summary>
public sealed class ServiceSchemaContractTests(PostgresFixture postgres) : ContractTests<TestDbContext>(postgres);
