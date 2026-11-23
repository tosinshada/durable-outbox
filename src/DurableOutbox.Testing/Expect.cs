namespace DurableOutbox.Testing;

/// <summary>
/// The contract's checks. A failure throws <see cref="InvalidOperationException"/> whose message starts with
/// the guarantee's number and name (section 5.3), so it reads the same in any test framework.
/// </summary>
internal static class Expect
{
    private static readonly string[] Guarantees =
    [
        "",
        "written with the change",
        "at least once",
        "one owner at a time",
        "a crash is an attempt",
        "never dropped",
    ];

    public static void That(int guarantee, bool condition, string what)
    {
        if (!condition)
        {
            throw Failure(guarantee, what);
        }
    }

    public static void Equal<T>(int guarantee, T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw Failure(guarantee, $"{what}: expected {expected}, found {actual}");
        }
    }

    public static void Sequence<T>(int guarantee, IEnumerable<T> expected, IEnumerable<T> actual, string what)
    {
        T[] wanted = [.. expected];
        T[] found = [.. actual];
        if (!wanted.SequenceEqual(found))
        {
            throw Failure(guarantee, $"{what}: expected [{string.Join(", ", wanted)}], found [{string.Join(", ", found)}]");
        }
    }

    public static async Task ThrowsAsync<TException>(int guarantee, Func<Task> action, string what) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw Failure(guarantee, $"{what}: no {typeof(TException).Name} was thrown");
    }

    private static InvalidOperationException Failure(int guarantee, string what) =>
        new($"Guarantee {guarantee} ({Guarantees[guarantee]}) does not hold: {what}.");
}
