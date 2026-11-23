using Xunit;

namespace DurableOutbox.Tests;

public sealed class RetryPolicyTests(PostgresFixture postgres) : WorkerTestBase(postgres)
{
    [Fact]
    public async Task A_failed_attempt_is_due_after_a_backoff_that_doubles_up_to_the_cap()
    {
        Handler.Behaviour = _ => Task.FromResult(OutboxResult.Retry("receiver busy"));
        await AddAsync(new OutboxMessage(Kind, "third"), new OutboxMessage(Kind, "tenth"));
        await SeedAttemptsAsync("third", 2);
        await SeedAttemptsAsync("tenth", 9);
        await StartWorkersAsync(outbox => outbox.AddHandler<TestHandler>(Kind, kind => kind.MaxAttempts = 20));

        await Handler.CallAsync(1);
        await StopWorkersAsync();

        // Base 5 seconds, cap 15 minutes, jitter 0.8 to 1.2.
        var now = Time.GetUtcNow();
        var third = await RowAsync("third");
        Assert.Equal(3, third.Attempts);
        Assert.InRange(third.NextAttemptAt - now, TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(24));
        var tenth = await RowAsync("tenth");
        Assert.Equal(10, tenth.Attempts);
        Assert.InRange(tenth.NextAttemptAt - now, TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(18));
    }

    [Fact]
    public async Task RetryAt_sets_the_next_due_time_and_uses_an_attempt()
    {
        Handler.Behaviour = _ => Task.FromResult(OutboxResult.RetryAt(Time.GetUtcNow() + TimeSpan.FromHours(1), "come back later"));
        await AddAsync(new OutboxMessage(Kind, "first"), new OutboxMessage(Kind, "last"));
        await SeedAttemptsAsync("last", 2);
        await StartWorkersAsync(outbox => outbox.AddHandler<TestHandler>(Kind, kind => kind.MaxAttempts = 3));

        await Handler.CallAsync(1);
        await StopWorkersAsync();

        var first = await RowAsync("first");
        Assert.Equal(OutboxStatus.Pending, first.Status);
        Assert.Equal(1, first.Attempts);
        Assert.Equal(Time.GetUtcNow() + TimeSpan.FromHours(1), first.NextAttemptAt);

        // The kind's attempt limit applies to RetryAt like any failure.
        var last = await RowAsync("last");
        Assert.Equal(OutboxStatus.Parked, last.Status);
        Assert.Equal(3, last.Attempts);
    }

    [Fact]
    public async Task Error_text_is_redacted_and_cut_before_it_is_stored_or_logged()
    {
        Handler.Behaviour = call => call.Delivery.RecordRef switch
        {
            "thrown" => throw new InvalidOperationException("call failed with token=abc"),
            "long" => Task.FromResult(OutboxResult.Retry(new string('x', 5000))),
            _ => Task.FromResult(OutboxResult.Retry("boom token=abc")),
        };
        await AddAsync(new OutboxMessage(Kind, "thrown"), new OutboxMessage(Kind, "long"), new OutboxMessage(Kind, "unredactable"));
        await StartWorkersAsync(outbox =>
        {
            outbox.RedactError = error => error.Contains("boom", StringComparison.Ordinal)
                ? throw new FormatException("the filter failed")
                : error.Replace("token=abc", "token=***", StringComparison.Ordinal);
            outbox.AddHandler<TestHandler>(Kind);
        });

        await Handler.CallAsync(2);
        await StopWorkersAsync();

        var thrown = (await RowAsync("thrown")).LastError!;
        Assert.Contains("call failed with token=***", thrown, StringComparison.Ordinal);
        Assert.DoesNotContain("token=abc", thrown, StringComparison.Ordinal);
        Assert.Contains(Logs.Entries, entry => entry.Contains("call failed with token=***", StringComparison.Ordinal));
        Assert.DoesNotContain(Logs.Entries, entry => entry.Contains("token=abc", StringComparison.Ordinal));

        Assert.Equal(new string('x', 2000), (await RowAsync("long")).LastError);
        Assert.Equal(ErrorText.CouldNotRedact, (await RowAsync("unredactable")).LastError);
    }
}
