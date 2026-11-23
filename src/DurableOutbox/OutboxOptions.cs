namespace DurableOutbox;

/// <summary>The options passed to <c>services.AddDurableOutbox&lt;TContext&gt;(…)</c>.</summary>
public sealed class OutboxOptions
{
    private readonly List<KindSettings> _kinds = [];

    /// <summary>How long a kind waits before claiming again after a claim found less than it asked for.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The service's filter for error text. Every error from a handler passes through it once before it
    /// is stored or logged. The default keeps the text unchanged.
    /// </summary>
    public Func<string, string> RedactError { get; set; } = error => error;

    /// <summary>False in a process that only writes messages.</summary>
    public bool RunWorkers { get; set; } = true;

    /// <summary>On shutdown, how long running handlers get to finish before their tokens are cancelled.</summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(20);

    internal IReadOnlyList<KindSettings> Kinds => _kinds;

    /// <summary>Registers the handler for one message kind, with that kind's options.</summary>
    public void AddHandler<THandler>(string kind, Action<OutboxKindOptions>? configure = null)
        where THandler : class, IOutboxHandler
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (_kinds.Exists(registered => registered.Kind == kind))
        {
            throw new ArgumentException($"A handler for kind '{kind}' is already registered.", nameof(kind));
        }

        var options = new OutboxKindOptions();
        configure?.Invoke(options);
        _kinds.Add(KindSettings.From(kind, typeof(THandler), options));
    }
}
