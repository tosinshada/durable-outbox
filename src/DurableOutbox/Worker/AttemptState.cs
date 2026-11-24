namespace DurableOutbox;

internal enum AttemptState
{
    /// <summary>Claimed; the handler has not been called yet.</summary>
    Starting,

    /// <summary>The handler is running. Its lease is renewed.</summary>
    Running,

    /// <summary>The handler has returned and its outcome is being written. Its lease is no longer renewed.</summary>
    Recording,
}
