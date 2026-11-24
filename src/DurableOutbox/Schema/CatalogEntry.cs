namespace DurableOutbox;

/// <summary>
/// One column or index of the outbox table, as the database catalog describes it. For a column,
/// <see cref="Detail"/> is its type and <see cref="Flag"/> is NOT NULL; for an index, its definition and
/// whether it is unique.
/// </summary>
internal sealed class CatalogEntry
{
    public string Kind { get; init; } = null!;
    public string Name { get; init; } = null!;
    public string Detail { get; init; } = null!;
    public bool Flag { get; init; }
}
