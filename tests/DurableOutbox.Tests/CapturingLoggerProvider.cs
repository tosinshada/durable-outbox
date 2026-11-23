using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DurableOutbox.Tests;

/// <summary>Keeps the library's log lines, with any exception text, so a test can read what was logged.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyList<string> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) =>
        categoryName == "DurableOutbox" ? new CapturingLogger(_entries) : NullLogger.Instance;

    public void Dispose()
    {
    }
}
