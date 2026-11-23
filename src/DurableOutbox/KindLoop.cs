using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DurableOutbox;

/// <summary>
/// The worker for one kind. The loop itself claims and renews, one statement at a time; each attempt
/// runs on its own task and scope and hands its slot back when its outcome is written.
/// </summary>
internal sealed class KindLoop<TContext> where TContext : DbContext
{
    /// <summary>How long a handler has to return after its timeout cancelled its token.</summary>
    private static readonly TimeSpan TimeoutGrace = TimeSpan.FromSeconds(5);

    private readonly KindSettings _kind;
    private readonly OutboxSettings _settings;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly TimeSpan _renewEvery;

    private readonly Lock _gate = new();
    private readonly HashSet<Attempt> _running = [];
    private TaskCompletionSource _wake = NewSignal();
    private TaskCompletionSource _idle = NewSignal();
    private bool _waiting;
    private bool _finished;

    // Touched by the loop only.
    private DateTimeOffset _nextClaim;
    private DateTimeOffset _nextRenewal;
    private int _claimFailures;

    public KindLoop(KindSettings kind, OutboxSettings settings, IServiceScopeFactory scopes, TimeProvider time, ILogger logger)
    {
        _kind = kind;
        _settings = settings;
        _scopes = scopes;
        _time = time;
        _logger = logger;
        _renewEvery = TimeSpan.FromTicks(Math.Max(kind.Lease.Ticks / 3, 1));
    }

    /// <summary>
    /// Completes while the loop is waiting on its clock and no attempt is starting or writing its outcome,
    /// so nothing moves until the clock does, a handler returns, or the host stops.
    /// </summary>
    internal Task Idle
    {
        get
        {
            lock (_gate)
            {
                return _idle.Task;
            }
        }
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        _nextClaim = _time.GetUtcNow();
        _nextRenewal = _nextClaim + _renewEvery;

        using var stopping = stoppingToken.Register(Wake);
        try
        {
            await ServeAsync(stoppingToken);
            await DrainAsync();
        }
        finally
        {
            lock (_gate)
            {
                _finished = true;
                UpdateIdle();
            }
        }
    }

    private async Task ServeAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            ResetWake();
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            var now = _time.GetUtcNow();
            if (now >= _nextRenewal)
            {
                await RenewAsync();
                continue;
            }

            var free = FreeSlots();
            if (free > 0 && now >= _nextClaim)
            {
                await ClaimAsync(free);
                continue;
            }

            await WaitAsync((free > 0 && _nextClaim < _nextRenewal ? _nextClaim : _nextRenewal) - now);
        }
    }

    /// <summary>Stops claiming, keeps renewing, and cancels what is still running once the drain period ends.</summary>
    private async Task DrainAsync()
    {
        var drainEnd = _time.GetUtcNow() + _settings.DrainTimeout;
        var cancelled = false;

        while (true)
        {
            ResetWake();
            Attempt[] running;
            lock (_gate)
            {
                running = [.. _running];
            }

            if (running.Length == 0)
            {
                return;
            }

            var now = _time.GetUtcNow();
            if (!cancelled && now >= drainEnd)
            {
                cancelled = true;
                foreach (var attempt in running)
                {
                    TryCancel(attempt, CancelReason.DrainEnded, whileHandlerRuns: false);
                }

                continue;
            }

            if (now >= _nextRenewal)
            {
                await RenewAsync();
                continue;
            }

            await WaitAsync((cancelled || _nextRenewal < drainEnd ? _nextRenewal : drainEnd) - now);
        }
    }

    private async Task ClaimAsync(int free)
    {
        var now = _time.GetUtcNow();
        ClaimBatch batch;
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            batch = await scope.ServiceProvider.GetRequiredService<OutboxStore<TContext>>().ClaimAsync(
                _kind.Kind, free, _kind.Lease, _kind.MaxAttempts, _kind.RetryWindow, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _claimFailures++;
            var delay = Backoff.Outage(_claimFailures, _settings.PollInterval);
            OutboxLog.DatabaseUnreachable(_logger, _kind.Kind, "claim", delay, exception);
            _nextClaim = now + delay;
            return;
        }

        _claimFailures = 0;
        foreach (var message in batch.Messages)
        {
            if (message.Status == OutboxStatus.Pending)
            {
                var delivery = new OutboxDelivery(
                    message.Id, _kind.Kind, message.RecordRef, message.Attempt, message.CreatedAt, message.Deadline);
                Start(new Attempt(delivery, batch.Token, now + _kind.Lease));
            }
            else
            {
                OutboxLog.EndedByClaim(_logger, message.Id, _kind.Kind, message.RecordRef, message.Status);
            }
        }

        // A full claim means more may be waiting: claim again as soon as a slot is free.
        _nextClaim = batch.Messages.Count >= free ? now : now + Backoff.Poll(_settings.PollInterval);
    }

    /// <summary>
    /// Extends the lease of every attempt whose handler is still running, in one statement. An attempt
    /// the statement did not renew, or whose lease end has passed without a renewal, has lost its lease.
    /// </summary>
    private async Task RenewAsync()
    {
        var now = _time.GetUtcNow();
        _nextRenewal = now + _renewEvery;

        Attempt[] held;
        lock (_gate)
        {
            held = [.. _running.Where(attempt => attempt.State != AttemptState.Recording)];
        }

        if (held.Length == 0)
        {
            return;
        }

        HashSet<Guid>? renewed = null;
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            renewed = [.. await scope.ServiceProvider.GetRequiredService<OutboxStore<TContext>>().RenewAsync(
                [.. held.Select(attempt => attempt.Id)],
                [.. held.Select(attempt => attempt.Token)],
                _kind.Lease,
                CancellationToken.None)];
        }
        catch (Exception exception)
        {
            OutboxLog.DatabaseUnreachable(_logger, _kind.Kind, "lease renewal", _renewEvery, exception);
        }

        var checkedAt = _time.GetUtcNow();
        foreach (var attempt in held)
        {
            bool lost;
            lock (_gate)
            {
                if (renewed?.Contains(attempt.Id) == true)
                {
                    attempt.LeaseEnd = now + _kind.Lease;
                }

                lost = (renewed is not null && !renewed.Contains(attempt.Id)) || attempt.LeaseEnd <= checkedAt;
            }

            if (lost && TryCancel(attempt, CancelReason.LeaseLost, whileHandlerRuns: true))
            {
                OutboxLog.LeaseLost(_logger, attempt.Id, _kind.Kind, attempt.Delivery.Attempt);
            }
        }
    }

    private void Start(Attempt attempt)
    {
        lock (_gate)
        {
            attempt.State = AttemptState.Starting;
            _running.Add(attempt);
            UpdateIdle();
        }

        if (_kind.AttemptTimeout is { } timeout)
        {
            attempt.StartTimeout(timeout, _time, () => TryCancel(attempt, CancelReason.TimedOut, whileHandlerRuns: false));
        }

        _ = RunAttemptAsync(attempt);
    }

    private async Task RunAttemptAsync(Attempt attempt)
    {
        var scope = _scopes.CreateAsyncScope();
        var handlerStillRunning = false;
        try
        {
            var handler = Task.Run(() => InvokeAsync(scope.ServiceProvider, attempt));

            // Stop renewing the moment the handler returns, before its outcome takes the row's lock.
            _ = handler.ContinueWith(
                _ => SetState(attempt, AttemptState.Recording),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            if (await Task.WhenAny(handler, attempt.TimedOut) != handler
                && await Task.WhenAny(handler, Task.Delay(TimeoutGrace, _time)) != handler)
            {
                // The handler ignored its token. Record the timeout, free the slot, and leave the handler
                // its scope until it ends; whatever it returns then is discarded.
                handlerStillRunning = true;
                SetState(attempt, AttemptState.Recording);
                OutboxLog.AttemptTimedOut(_logger, attempt.Id, _kind.Kind, attempt.Delivery.Attempt);
                await using (var fresh = _scopes.CreateAsyncScope())
                {
                    await FailAsync(fresh.ServiceProvider, attempt, Error(ErrorText.TimedOut), NextAttempt(attempt));
                }

                _ = ObserveLateHandlerAsync(handler, scope, attempt);
                return;
            }

            SetState(attempt, AttemptState.Recording);
            await RecordAsync(scope.ServiceProvider, attempt, handler);
        }
        catch (Exception exception)
        {
            OutboxLog.OutcomeWriteFailed(_logger, attempt.Id, _kind.Kind, attempt.Delivery.Attempt, exception);
        }
        finally
        {
            if (!handlerStillRunning)
            {
                await scope.DisposeAsync();
                attempt.Dispose();
            }

            Release(attempt);
        }
    }

    private async Task<OutboxResult> InvokeAsync(IServiceProvider services, Attempt attempt)
    {
        Task<OutboxResult> pending;
        try
        {
            var handler = (IOutboxHandler)services.GetRequiredService(_kind.HandlerType);
            pending = handler.HandleAsync(attempt.Delivery, attempt.HandlerToken);
        }
        finally
        {
            SetState(attempt, AttemptState.Running);
        }

        return await pending ?? throw new InvalidOperationException("The handler returned no result.");
    }

    private async Task ObserveLateHandlerAsync(Task<OutboxResult> handler, AsyncServiceScope scope, Attempt attempt)
    {
        try
        {
            await handler;
        }
        catch (Exception exception)
        {
            OutboxLog.HandlerFailed(
                _logger, attempt.Id, _kind.Kind, attempt.Delivery.Attempt, Error(exception.ToString()));
        }
        finally
        {
            await scope.DisposeAsync();
            attempt.Dispose();
        }
    }

    private async Task RecordAsync(IServiceProvider services, Attempt attempt, Task<OutboxResult> handler)
    {
        OutboxResult result;
        try
        {
            result = await handler;
        }
        catch (Exception exception)
        {
            await RecordFailureAsync(services, attempt, exception);
            return;
        }

        if (ReasonOf(attempt) == CancelReason.LeaseLost)
        {
            return;
        }

        switch (result)
        {
            case DoneWithRecordResult done:
                await CompleteWithRecordAsync(services, attempt, done.Record);
                break;
            case RetryResult retry:
                await FailAsync(services, attempt, Error(retry.Error), retry.At ?? NextAttempt(attempt));
                break;
            case RejectResult reject:
                await WriteAsync(services, attempt, store => store.RejectAsync(
                    attempt.Id, attempt.Token, Error(reject.Reason), CancellationToken.None));
                break;
            default:
                await WriteAsync(services, attempt, store => store.CompleteAsync(
                    attempt.Id, attempt.Token, CancellationToken.None));
                break;
        }
    }

    /// <summary>An exception counts as a retry, unless the token was cancelled because the lease was lost or the host stopped.</summary>
    private async Task RecordFailureAsync(IServiceProvider services, Attempt attempt, Exception exception)
    {
        switch (ReasonOf(attempt))
        {
            case CancelReason.LeaseLost or CancelReason.DrainEnded:
                return;
            case CancelReason.TimedOut:
                OutboxLog.AttemptTimedOut(_logger, attempt.Id, _kind.Kind, attempt.Delivery.Attempt);
                await FailAsync(services, attempt, Error(ErrorText.TimedOut), NextAttempt(attempt));
                return;
            default:
                var error = Error(exception.ToString());
                OutboxLog.HandlerFailed(_logger, attempt.Id, _kind.Kind, attempt.Delivery.Attempt, error);
                await FailAsync(services, attempt, error, NextAttempt(attempt));
                return;
        }
    }

    /// <summary>
    /// The fenced completion first, then the handler's follow-up writes, in one transaction opened after
    /// the handler returned. If the completion matches no row, <paramref name="record"/> never runs.
    /// </summary>
    private async Task CompleteWithRecordAsync(IServiceProvider services, Attempt attempt, Func<CancellationToken, Task> record)
    {
        var context = services.GetRequiredService<TContext>();
        var store = services.GetRequiredService<OutboxStore<TContext>>();

        bool completed;
        try
        {
            completed = await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(CancellationToken.None);
                if (!await store.CompleteAsync(attempt.Id, attempt.Token, CancellationToken.None))
                {
                    return false;
                }

                await record(attempt.HandlerToken);
                await context.SaveChangesAsync(attempt.HandlerToken);
                await transaction.CommitAsync(CancellationToken.None);
                return true;
            });
        }
        catch (Exception exception)
        {
            context.ChangeTracker.Clear();
            await RecordFailureAsync(services, attempt, exception);
            return;
        }

        if (!completed)
        {
            OutboxLog.OutcomeRefused(_logger, attempt.Id, _kind.Kind, attempt.Delivery.Attempt);
        }
    }

    private Task FailAsync(IServiceProvider services, Attempt attempt, string error, DateTimeOffset nextAttemptAt) =>
        WriteAsync(services, attempt, async store => await store.FailAsync(
            attempt.Id, attempt.Token, error, nextAttemptAt, _kind.MaxAttempts, _kind.RetryWindow, CancellationToken.None)
            != FailOutcome.Refused);

    /// <summary>
    /// Writes an outcome with no cancellation token. A refused outcome means the lease was lost. A failed
    /// write is not retried: the message comes back after its lease as an abandoned attempt.
    /// </summary>
    private async Task WriteAsync(IServiceProvider services, Attempt attempt, Func<OutboxStore<TContext>, Task<bool>> write)
    {
        try
        {
            if (!await write(services.GetRequiredService<OutboxStore<TContext>>()))
            {
                OutboxLog.OutcomeRefused(_logger, attempt.Id, _kind.Kind, attempt.Delivery.Attempt);
            }
        }
        catch (Exception exception)
        {
            OutboxLog.OutcomeWriteFailed(_logger, attempt.Id, _kind.Kind, attempt.Delivery.Attempt, exception);
        }
    }

    private string Error(string text) => ErrorText.From(text, _settings.RedactError);

    private DateTimeOffset NextAttempt(Attempt attempt) =>
        _time.GetUtcNow() + Backoff.RetryDelay(attempt.Delivery.Attempt, _kind.BackoffBase, _kind.BackoffCap);

    /// <summary>
    /// Cancels the attempt's token for <paramref name="reason"/>, unless it was already cancelled.
    /// A lost lease only stops a handler that is still running.
    /// </summary>
    private bool TryCancel(Attempt attempt, CancelReason reason, bool whileHandlerRuns)
    {
        lock (_gate)
        {
            if (attempt.Reason != CancelReason.None || (whileHandlerRuns && attempt.State == AttemptState.Recording))
            {
                return false;
            }

            attempt.Reason = reason;
        }

        attempt.Cancel();
        return true;
    }

    private CancelReason ReasonOf(Attempt attempt)
    {
        lock (_gate)
        {
            return attempt.Reason;
        }
    }

    private void SetState(Attempt attempt, AttemptState state)
    {
        lock (_gate)
        {
            if (state > attempt.State)
            {
                attempt.State = state;
            }

            UpdateIdle();
        }
    }

    private int FreeSlots()
    {
        lock (_gate)
        {
            return _kind.MaxConcurrency - _running.Count;
        }
    }

    private void Release(Attempt attempt)
    {
        lock (_gate)
        {
            _running.Remove(attempt);
            UpdateIdle();
        }

        Wake();
    }

    /// <summary>Starts a fresh wake signal before the loop reads its state, so a change after the read wakes it.</summary>
    private void ResetWake()
    {
        lock (_gate)
        {
            if (_wake.Task.IsCompleted)
            {
                _wake = NewSignal();
            }
        }
    }

    private async Task WaitAsync(TimeSpan delay)
    {
        // The timer exists before the loop counts as waiting, so a clock moved after that is seen by it.
        using var timer = _time.CreateTimer(
            static state => ((KindLoop<TContext>)state!).Wake(), this, delay, Timeout.InfiniteTimeSpan);

        Task wake;
        lock (_gate)
        {
            wake = _wake.Task;
            if (!wake.IsCompleted)
            {
                _waiting = true;
                UpdateIdle();
            }
        }

        await wake;
    }

    private void Wake()
    {
        TaskCompletionSource wake;
        lock (_gate)
        {
            wake = _wake;
            _waiting = false;
            UpdateIdle();
        }

        wake.TrySetResult();
    }

    private void UpdateIdle()
    {
        var idle = _finished || (_waiting && _running.All(attempt => attempt.State == AttemptState.Running));
        if (idle)
        {
            _idle.TrySetResult();
        }
        else if (_idle.Task.IsCompleted)
        {
            _idle = NewSignal();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
