using Xunit;

namespace DurableOutbox.Tests;

public sealed class RenewalTests(PostgresFixture postgres) : OutboxTestBase(postgres)
{
    // Guarantee 3: renewal is fenced per message by the token of the claim that holds it.
    [Fact]
    public async Task Renewal_extends_messages_from_several_claims_and_skips_one_held_under_another_token()
    {
        await AddAsync(new OutboxMessage(Kind, "order-1"), new OutboxMessage(Kind, "order-2"), new OutboxMessage(Kind, "order-3"));
        var first = await ClaimAsync(limit: 1);
        var second = await ClaimAsync(limit: 1);
        var third = await ClaimAsync(limit: 1);
        var ids = new[] { first.Messages[0].Id, second.Messages[0].Id, third.Messages[0].Id };
        await ReplaceTokenAsync(ids[2]);
        var leaseOfThird = (await RowAsync(third.Messages[0].RecordRef)).NextAttemptAt;

        Time.Advance(TimeSpan.FromSeconds(30));
        var renewed = await RenewAsync(ids, [first.Token, second.Token, third.Token]);

        Assert.Equal(ids[..2].Order(), renewed.Order());
        Assert.Equal(Time.GetUtcNow() + Lease, (await RowAsync(first.Messages[0].RecordRef)).NextAttemptAt);
        Assert.Equal(Time.GetUtcNow() + Lease, (await RowAsync(second.Messages[0].RecordRef)).NextAttemptAt);
        Assert.Equal(leaseOfThird, (await RowAsync(third.Messages[0].RecordRef)).NextAttemptAt);
    }
}
