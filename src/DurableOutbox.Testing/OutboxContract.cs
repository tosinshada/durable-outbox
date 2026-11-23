using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DurableOutbox.Testing;

/// <summary>
/// The guarantees of section 5.3 as methods a service runs against its own <typeparamref name="TContext"/>,
/// schema and table, from any test framework. Each method completes when its guarantee holds and throws
/// <see cref="InvalidOperationException"/>, naming the guarantee and what differed, when it does not.
/// </summary>
/// <remarks>
/// Each call uses a fake clock of its own and kinds it generates, so it never waits on real time, calls can run
/// at the same time on one database, and the service's own messages are never touched. The rows it writes stay
/// in the table, so point it at a database used only for tests.
/// </remarks>
public sealed class OutboxContract<TContext> where TContext : DbContext
{
    private readonly Action<IServiceCollection> _addDbContext;

    /// <param name="addDbContext">
    /// Registers <typeparamref name="TContext"/> against a database whose outbox table the service's own
    /// migrations created, for example <c>services =&gt; services.AddDbContext&lt;AppDbContext&gt;(o =&gt; o.UseNpgsql(cs))</c>.
    /// </param>
    public OutboxContract(Action<IServiceCollection> addDbContext)
    {
        ArgumentNullException.ThrowIfNull(addDbContext);
        _addDbContext = addDbContext;
    }

    /// <summary>Guarantee 1: a message commits with the business change or not at all, and is refused outside a transaction.</summary>
    public async Task WrittenWithTheChangeAsync(CancellationToken cancellationToken = default)
    {
        await using var host = Host(cancellationToken);

        await host.InScopeAsync(async scope =>
        {
            var context = scope.GetRequiredService<TContext>();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            Expect.That(1, await scope.GetRequiredService<IOutbox>().AddAsync(new OutboxMessage(host.Kind, "rolled-back"), cancellationToken),
                "AddAsync in an open transaction did not queue the message");
            await transaction.RollbackAsync(cancellationToken);
            return true;
        });
        Expect.Equal(1, 0, (await host.RowsAsync()).Count, "messages left by a transaction that rolled back");

        await host.AddAsync("committed");
        Expect.Sequence(1, ["committed"], (await host.RowsAsync()).Select(row => row.RecordRef), "messages after a commit");

        await Expect.ThrowsAsync<InvalidOperationException>(1,
            () => host.InScopeAsync(scope => scope.GetRequiredService<IOutbox>().AddAsync(new OutboxMessage(host.Kind, "no-transaction"), cancellationToken)),
            "AddAsync with no transaction open");
        Expect.Equal(1, 1, (await host.RowsAsync()).Count, "messages after AddAsync with no transaction");
    }

    /// <summary>
    /// Guarantee 2: a committed message is attempted until it is done, with the same ID every time, and a duplicate
    /// uniqueness key is not queued.
    /// </summary>
    public async Task AtLeastOnceAsync(CancellationToken cancellationToken = default)
    {
        await DuplicateIsIgnoredAsync(cancellationToken);
        await AttemptedUntilDoneAsync(cancellationToken);
    }

    /// <summary>
    /// Guarantee 3: a live claim is never given to a second worker, and an outcome from a claim that was taken
    /// over is refused.
    /// </summary>
    public async Task OneOwnerAtATimeAsync(CancellationToken cancellationToken = default)
    {
        await ConcurrentClaimersTakeEachMessageOnceAsync(cancellationToken);
        await StaleOutcomeIsRefusedAsync(cancellationToken);
        await RenewedLeaseIsNotTakenOverAsync(cancellationToken);
    }

    /// <summary>Guarantee 4: an attempt that never reports counts against the limit, so the message ends up parked.</summary>
    public async Task CrashIsAnAttemptAsync(CancellationToken cancellationToken = default)
    {
        await using var host = Host(cancellationToken);
        await host.AddAsync("order-1");
        var tokens = new List<Guid>();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var batch = await host.ClaimAsync(maxAttempts: 3);
            Expect.Equal(4, 1, batch.Messages.Count, $"messages claimed for attempt {attempt}");
            Expect.Equal(4, OutboxStatus.Pending, batch.Messages[0].Status, $"status claimed for attempt {attempt}");
            Expect.Equal(4, attempt, batch.Messages[0].Attempt, "attempt number after an attempt that never reported");
            tokens.Add(batch.Token);
            host.Time.Advance(ContractHost<TContext>.Lease);
        }

        Expect.Equal(4, 3, tokens.Distinct().Count(), "distinct lease tokens over three claims");

        var parked = await host.ClaimAsync(maxAttempts: 3);
        Expect.Equal(4, OutboxStatus.Parked, parked.Messages.Single().Status, "status returned by the claim after the last attempt");

        var row = (await host.RowsAsync()).Single();
        Expect.Equal(4, OutboxStatus.Parked, row.Status, "status after three attempts that never reported");
        Expect.Equal(4, 3, row.Attempts, "attempts recorded");
        Expect.Equal(4, null, row.LeaseToken, "lease token of the parked message");
        Expect.Equal(4, host.Time.GetUtcNow(), row.FinishedAt, "finish time of the parked message");
        Expect.Equal(4, OutboxStore<TContext>.AbandonedError, row.LastError, "last error");
        Expect.Sequence(4, [1, 2, 3], ContractHost<TContext>.Errors(row).Select(error => error.Attempt), "abandoned attempts in the error history");

        host.Time.Advance(TimeSpan.FromDays(1));
        Expect.Equal(4, 0, (await host.ClaimAsync(maxAttempts: 3)).Messages.Count, "claims of a parked message a day later");
    }

    /// <summary>
    /// Guarantee 5: a message that cannot be delivered is parked with the error from each attempt, and an operator
    /// retry makes it claimable again with its attempts reset and its errors kept.
    /// </summary>
    public async Task NeverDroppedAsync(CancellationToken cancellationToken = default)
    {
        await using var host = Host(cancellationToken);
        await host.AddAsync("order-1");

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var batch = await host.ClaimAsync(maxAttempts: 3);
            var outcome = await host.FailAsync(batch.Messages.Single().Id, batch.Token, $"failure {attempt}", maxAttempts: 3);
            Expect.Equal(5, attempt < 3 ? FailOutcome.Retried : FailOutcome.Parked, outcome, $"outcome of failed attempt {attempt}");
        }

        var row = (await host.RowsAsync()).Single();
        (int, string)[] errors = [(1, "failure 1"), (2, "failure 2"), (3, "failure 3")];
        Expect.Equal(5, OutboxStatus.Parked, row.Status, "status after every attempt failed");
        Expect.Equal(5, host.Time.GetUtcNow(), row.FinishedAt, "finish time of the parked message");
        Expect.Equal(5, null, row.LeaseToken, "lease token of the parked message");
        Expect.Sequence(5, errors, ContractHost<TContext>.Errors(row), "error history");

        host.Time.Advance(TimeSpan.FromDays(1));
        Expect.Equal(5, 0, (await host.ClaimAsync(maxAttempts: 3)).Messages.Count, "claims of a parked message a day later");

        Expect.That(5, await host.InScopeAsync(scope => scope.GetRequiredService<IOutboxAdmin>().RetryAsync(row.Id, cancellationToken)),
            "an operator retry of a parked message was refused");
        var retried = await host.ClaimAsync(maxAttempts: 3);
        Expect.Equal(5, row.Id, retried.Messages.Single().Id, "message claimed after an operator retry");
        Expect.Equal(5, 1, retried.Messages[0].Attempt, "attempt number after an operator retry");
        Expect.Sequence(5, errors, ContractHost<TContext>.Errors((await host.RowsAsync()).Single()), "error history after an operator retry");
    }

    private async Task DuplicateIsIgnoredAsync(CancellationToken cancellationToken)
    {
        await using var host = Host(cancellationToken);
        await host.InScopeAsync(async scope =>
        {
            var context = scope.GetRequiredService<TContext>();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await scope.GetRequiredService<IOutbox>().AddAsync(new OutboxMessage(host.Kind, "order-1") { UniqueKey = "order-1" }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        });

        await host.InScopeAsync(async scope =>
        {
            var context = scope.GetRequiredService<TContext>();
            var outbox = scope.GetRequiredService<IOutbox>();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            Expect.That(2, !await outbox.AddAsync(new OutboxMessage(host.Kind, "order-1") { UniqueKey = "order-1" }, cancellationToken),
                "a duplicate kind and uniqueness key was queued");
            Expect.That(2, await outbox.AddAsync(new OutboxMessage(host.OtherKind, "order-1") { UniqueKey = "order-1" }, cancellationToken),
                "the same uniqueness key under another kind was not queued");
            await transaction.CommitAsync(cancellationToken);
            return true;
        });

        // The transaction that met the duplicate still committed its other write.
        Expect.Sequence(2, new[] { host.Kind, host.OtherKind }.Order(StringComparer.Ordinal), (await host.RowsAsync()).Select(row => row.Kind).Order(StringComparer.Ordinal),
            "kinds queued after a duplicate");
    }

    private async Task AttemptedUntilDoneAsync(CancellationToken cancellationToken)
    {
        await using var host = Host(cancellationToken);
        host.Script.Behaviour = call => call.Delivery.Attempt switch
        {
            1 => throw new InvalidOperationException("receiver down"),
            2 => Task.FromResult(OutboxResult.Retry("receiver busy")),
            _ => Task.FromResult(OutboxResult.Done()),
        };
        await host.AddAsync("order-1");
        await host.StartWorkersAsync();

        await host.Clock.AdvanceUntilAsync(host.Script.CallAsync(2), ContractHost<TContext>.Step);
        await host.StopWorkersAsync();

        var calls = host.Script.Calls;
        Expect.Sequence(2, [1, 2, 3], calls.Select(call => call.Delivery.Attempt), "attempt numbers given to the handler");
        Expect.Equal(2, 1, calls.Select(call => call.Delivery.Id).Distinct().Count(), "distinct message IDs over three attempts");
        Expect.Equal(2, 3, calls.Select(call => call.Handler).Distinct().Count(), "handler instances over three attempts, one per scope");

        var row = (await host.RowsAsync()).Single();
        Expect.Equal(2, calls[0].Delivery.Id, row.Id, "message ID given to the handler");
        Expect.Equal(2, OutboxStatus.Completed, row.Status, "status after the handler returned done");
        var errors = ContractHost<TContext>.Errors(row);
        Expect.Sequence(2, [1, 2], errors.Select(error => error.Attempt), "attempts in the error history");
        Expect.That(2, errors[0].Error.Contains("InvalidOperationException: receiver down", StringComparison.Ordinal),
            $"the first error is not the exception the handler threw: {errors[0].Error}");
        Expect.Equal(2, "receiver busy", errors[1].Error, "second error");
    }

    private async Task ConcurrentClaimersTakeEachMessageOnceAsync(CancellationToken cancellationToken)
    {
        const int total = 1_000;
        await using var host = Host(cancellationToken);
        await host.AddAsync([.. Enumerable.Range(0, total).Select(i => $"order-{i}")]);

        var claimed = new ConcurrentBag<Guid>();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (true)
            {
                var batch = await host.ClaimAsync(limit: 25);
                if (batch.Messages.Count == 0)
                {
                    return;
                }

                foreach (var message in batch.Messages)
                {
                    claimed.Add(message.Id);
                }
            }
        }, cancellationToken)));

        var twice = claimed.GroupBy(id => id).Where(group => group.Count() > 1).Select(group => group.Key).FirstOrDefault();
        Expect.That(3, twice == Guid.Empty, $"message {twice} was claimed by two claimers at once");
        Expect.Equal(3, total, claimed.Count, "messages claimed by four concurrent claimers");
    }

    private async Task StaleOutcomeIsRefusedAsync(CancellationToken cancellationToken)
    {
        await using var host = Host(cancellationToken);
        await host.AddAsync("order-1");
        var stale = await host.ClaimAsync();
        var id = stale.Messages.Single().Id;

        host.Time.Advance(ContractHost<TContext>.Lease);
        var current = await host.ClaimAsync();
        Expect.Equal(3, id, current.Messages.Single().Id, "message claimed again after its lease ran out");

        Expect.That(3, !await host.CompleteAsync(id, stale.Token), "a completion under a stale token was accepted");
        Expect.Equal(3, FailOutcome.Refused, await host.FailAsync(id, stale.Token, "late failure"), "failure under a stale token");
        Expect.That(3, !await host.RejectAsync(id, stale.Token, "late refusal"), "a rejection under a stale token was accepted");

        var row = (await host.RowsAsync()).Single();
        Expect.Equal(3, OutboxStatus.Pending, row.Status, "status after stale outcomes");
        Expect.Equal(3, current.Token, row.LeaseToken, "lease token after stale outcomes");
        Expect.Equal(3, 2, row.Attempts, "attempts after stale outcomes");
        Expect.Equal(3, OutboxStore<TContext>.AbandonedError, row.LastError, "last error after stale outcomes");

        Expect.That(3, await host.CompleteAsync(id, current.Token), "the current owner's completion was refused");
        Expect.That(3, !await host.CompleteAsync(id, current.Token), "a second completion was accepted");
        Expect.Equal(3, OutboxStatus.Completed, (await host.RowsAsync()).Single().Status, "status after the current owner completed it");
    }

    private async Task RenewedLeaseIsNotTakenOverAsync(CancellationToken cancellationToken)
    {
        await using var host = Host(cancellationToken);
        var step = ContractHost<TContext>.Step;
        host.Script.Behaviour = call => call.ReleasedAsync();
        await host.AddAsync("order-1");
        await host.StartWorkersAsync();
        await host.Clock.AdvanceUntilAsync(host.Script.CallAsync(0), step);

        // A second claim, ten seconds later, so the two attempts carry different tokens.
        await host.Clock.AdvanceAsync(TimeSpan.FromSeconds(10), step);
        await host.AddAsync("order-2");
        await host.Clock.AdvanceUntilAsync(host.Script.CallAsync(1), step);

        for (var elapsed = 0; elapsed < 150; elapsed += 20)
        {
            await host.Clock.AdvanceAsync(TimeSpan.FromSeconds(20), step);
            Expect.Equal(3, 0, (await host.ClaimAsync()).Messages.Count, $"messages a second claimer took {elapsed + 20} seconds into renewed leases");
        }

        foreach (var call in host.Script.Calls)
        {
            call.Release(OutboxResult.Done());
        }

        await host.StopWorkersAsync();

        Expect.Equal(3, 2, host.Script.Calls.Count, "handler calls");
        foreach (var row in await host.RowsAsync())
        {
            Expect.Equal(3, OutboxStatus.Completed, row.Status, $"status of {row.RecordRef} once its handler returned done");
            Expect.Equal(3, 1, row.Attempts, $"attempts of {row.RecordRef}");
        }
    }

    private ContractHost<TContext> Host(CancellationToken cancellationToken) => new(_addDbContext, cancellationToken);
}
