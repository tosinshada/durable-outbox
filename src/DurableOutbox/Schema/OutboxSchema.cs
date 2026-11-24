using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace DurableOutbox;

/// <summary>
/// The table the library expects (section 9.3), and the start-up check that compares it with the database
/// catalog. Kept beside <see cref="OutboxModelBuilderExtensions"/>: a change to one is a change to the other.
/// </summary>
internal static partial class OutboxSchema
{
    /// <summary>Reads the table's columns and indexes. Only reads the catalog.</summary>
    private const string Catalog = """
        SELECT 'column' AS "Kind", a.attname AS "Name", format_type(a.atttypid, a.atttypmod) AS "Detail",
               a.attnotnull AS "Flag"
          FROM pg_attribute a
         WHERE a.attrelid = to_regclass(@table) AND a.attnum > 0 AND NOT a.attisdropped
        UNION ALL
        SELECT 'index', c.relname, pg_get_indexdef(i.indexrelid), i.indisunique
          FROM pg_index i
          JOIN pg_class c ON c.oid = i.indexrelid
         WHERE i.indrelid = to_regclass(@table)
        """;

    private static readonly (string Name, string Type, bool NotNull)[] Columns =
    [
        ("id", "uuid", true),
        ("kind", "text", true),
        ("record_ref", "text", true),
        ("unique_key", "text", false),
        ("priority", "integer", true),
        ("status", "text", true),
        ("attempts", "integer", true),
        ("next_attempt_at", "timestamp with time zone", true),
        ("lease_token", "uuid", false),
        ("deadline_at", "timestamp with time zone", false),
        ("created_at", "timestamp with time zone", true),
        ("finished_at", "timestamp with time zone", false),
        ("last_error", "text", false),
        ("errors", "jsonb", true),
        ("trace_parent", "text", false),
    ];

    /// <summary>Indexes are matched by shape, not name. Filters are written as PostgreSQL prints them.</summary>
    private static readonly (string Description, string Columns, bool Unique, string Filter)[] Indexes =
    [
        ("the primary key on (id)", "id", true, ""),
        ("the claim index on (kind, priority DESC, next_attempt_at) WHERE status = 'Pending'",
            "kind, priority DESC, next_attempt_at", false, "(status = 'Pending'::text)"),
        ("the unique index on (kind, unique_key) WHERE unique_key IS NOT NULL",
            "kind, unique_key", true, "(unique_key IS NOT NULL)"),
        ("the operator index on (kind, finished_at) WHERE status IN ('Parked', 'Rejected')",
            "kind, finished_at", false, "(status = ANY (ARRAY['Parked'::text, 'Rejected'::text]))"),
    ];

    /// <summary>Every way the table differs from what the library expects. Empty when it matches.</summary>
    public static async Task<List<string>> FindDifferencesAsync(DbContext context, OutboxSql sql, CancellationToken cancellationToken)
    {
        var catalog = await context.Database
            .SqlQueryRaw<CatalogEntry>(Catalog, new NpgsqlParameter("table", NpgsqlDbType.Text) { Value = sql.Table })
            .ToListAsync(cancellationToken);

        return Compare(catalog);
    }

    /// <summary>The error a mismatch fails with, naming the table and every difference.</summary>
    public static InvalidOperationException Mismatch(OutboxSql sql, IReadOnlyList<string> differences) => new(
        $"The outbox table {sql.Table} does not match what DurableOutbox expects: {string.Join("; ", differences)}. "
        + "Create it with a migration generated from modelBuilder.AddDurableOutbox(...).");

    private static List<string> Compare(List<CatalogEntry> catalog)
    {
        if (catalog.Count == 0)
        {
            return ["the table does not exist"];
        }

        var differences = new List<string>();
        var columns = catalog.Where(entry => entry.Kind == "column").ToDictionary(entry => entry.Name);
        foreach (var (name, type, notNull) in Columns)
        {
            if (!columns.TryGetValue(name, out var column))
            {
                differences.Add($"column {name} is missing");
                continue;
            }

            if (column.Detail != type)
            {
                differences.Add($"column {name} is {column.Detail}, not {type}");
            }

            if (column.Flag != notNull)
            {
                differences.Add($"column {name} {(notNull ? "allows nulls" : "does not allow nulls")}");
            }
        }

        var indexes = catalog
            .Where(entry => entry.Kind == "index")
            .Select(entry => (Shape: Shape(entry.Detail), entry.Flag))
            .ToList();

        foreach (var (description, expectedColumns, unique, filter) in Indexes)
        {
            var expected = (Normalize(expectedColumns), Normalize(filter));
            if (!indexes.Exists(index => index.Shape == expected && index.Flag == unique))
            {
                differences.Add($"{description} is missing");
            }
        }

        return differences;
    }

    /// <summary>The columns and filter of a btree index definition from <c>pg_get_indexdef</c>.</summary>
    private static (string Columns, string Filter)? Shape(string definition)
    {
        var match = IndexDefinition().Match(definition);
        return match.Success && match.Groups["method"].Value == "btree"
            ? (Normalize(match.Groups["columns"].Value), Normalize(match.Groups["filter"].Value))
            : null;
    }

    /// <summary>Removes what PostgreSQL versions may print differently: spacing, parentheses, quotes and text casts.</summary>
    private static string Normalize(string text) => Noise().Replace(text, "");

    [GeneratedRegex(@"USING (?<method>\w+) \((?<columns>[^)]*)\)(?: INCLUDE \([^)]*\))?(?: WHERE (?<filter>.*))?$")]
    private static partial Regex IndexDefinition();

    [GeneratedRegex(@"\s+|[()""]|::text")]
    private static partial Regex Noise();
}
