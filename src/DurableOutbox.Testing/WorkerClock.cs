using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace DurableOutbox.Testing;

/// <summary>
/// Moves a fake clock for running workers, one step at a time and only while they are idle: every loop is
/// waiting on the clock with no attempt starting or writing its outcome, the start-up check is not querying,
/// and no gauge sample is being taken. So nothing waits on real time, and the clock never races ahead of
/// database work the caller cannot see.
/// </summary>
internal sealed class WorkerClock(FakeTimeProvider time, Func<Task> idle, CancellationToken cancellationToken)
{
    /// <summary>A wait that has not ended after this many steps has failed, rather than running forever.</summary>
    public const int MaxSteps = 1_000;

    /// <summary>A clock for the workers and samplers among <paramref name="hosted"/>.</summary>
    public static WorkerClock For<TContext>(FakeTimeProvider time, IReadOnlyList<IHostedService> hosted, CancellationToken cancellationToken)
        where TContext : DbContext =>
        new(time,
            () => Task.WhenAll(
                hosted.OfType<OutboxWorker<TContext>>().Select(worker => worker.IdleAsync())
                    .Concat(hosted.OfType<OutboxSampler<TContext>>().Select(sampler => sampler.Idle))),
            cancellationToken);

    public Task IdleAsync() => idle().WaitAsync(cancellationToken);

    /// <summary>
    /// Moves the clock in steps, each once the workers are idle, until <paramref name="signal"/> completes.
    /// The signal must be something the clock brings about. Idle workers still move without it when a handler
    /// returns or the host stops, and the steps run in a few milliseconds of real time, so a signal that waits
    /// on that work can run out of steps first: await such work after the clock's part is done.
    /// </summary>
    public async Task AdvanceUntilAsync(Task signal, TimeSpan step)
    {
        for (var steps = 0; !signal.IsCompleted; steps++)
        {
            await IdleAsync();
            if (signal.IsCompleted)
            {
                break;
            }

            Advance(step, steps);
            await Task.Yield();
        }

        await signal;
    }

    /// <summary>Moves the clock in steps, each once the workers are idle, until <paramref name="condition"/> holds.</summary>
    public async Task AdvanceUntilAsync(Func<Task<bool>> condition, TimeSpan step)
    {
        for (var steps = 0; ; steps++)
        {
            await IdleAsync();
            if (await condition())
            {
                return;
            }

            Advance(step, steps);
        }
    }

    /// <summary>Moves the clock forward by <paramref name="by"/>, in steps taken while the workers are idle.</summary>
    public async Task AdvanceAsync(TimeSpan by, TimeSpan step)
    {
        var until = time.GetUtcNow() + by;
        while (time.GetUtcNow() < until)
        {
            await IdleAsync();
            var left = until - time.GetUtcNow();
            time.Advance(left < step ? left : step);
            await Task.Yield();
        }

        await IdleAsync();
    }

    private void Advance(TimeSpan step, int stepsTaken)
    {
        if (stepsTaken >= MaxSteps)
        {
            throw new InvalidOperationException($"Nothing happened after moving the clock {MaxSteps} times by {step}.");
        }

        time.Advance(step);
    }
}
