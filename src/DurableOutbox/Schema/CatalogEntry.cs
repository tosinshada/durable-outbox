using System.ComponentModel.DataAnnotations.Schema;

namespace DurableOutbox;

/// <summary>
/// One column or index of the outbox table, as the database catalog describes it. For a column,
/// <see cref="Detail"/> is its type and <see cref="Flag"/> is NOT NULL; for an index, its definition and
/// whether it is unique.
/// </summary>
internal sealed class CatalogEntry
{
    [Column("Kind")]
    public string Kind { get; init; } = null!;
    [Column("Name")]
    public string Name { get; init; } = null!;
    [Column("Detail")]
    public string Detail { get; init; } = null!;
    [Column("Flag")]
    public bool Flag { get; init; }
}
