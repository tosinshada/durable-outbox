using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DurableOutbox.Tests;

public sealed class CapturingLogger(ConcurrentQueue<string> entries) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        entries.Enqueue(exception is null ? formatter(state, exception) : $"{formatter(state, exception)}\n{exception}");
}
