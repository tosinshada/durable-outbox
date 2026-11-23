using Microsoft.EntityFrameworkCore;

namespace DurableOutbox;

/// <summary>Adds the outbox table to a service's EF Core model.</summary>
public static class OutboxModelBuilderExtensions
{
    /// <summary>
    /// Adds the outbox table to the service's model, so its own migrations create it.
    /// Column names are fixed. Only the schema and table name are the service's choice.
    /// </summary>
    public static ModelBuilder AddDurableOutbox(this ModelBuilder modelBuilder, string? schema, string table)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        modelBuilder.Entity<OutboxMessageRow>(row =>
        {
            row.ToTable(table, schema);
            row.HasKey(x => x.Id).HasName($"pk_{table}");

            row.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").ValueGeneratedNever();
            row.Property(x => x.Kind).HasColumnName("kind").HasColumnType("text");
            row.Property(x => x.RecordRef).HasColumnName("record_ref").HasColumnType("text");
            row.Property(x => x.UniqueKey).HasColumnName("unique_key").HasColumnType("text");
            row.Property(x => x.Priority).HasColumnName("priority").HasColumnType("integer").HasDefaultValue(0);
            row.Property(x => x.Status).HasColumnName("status").HasColumnType("text");
            row.Property(x => x.Attempts).HasColumnName("attempts").HasColumnType("integer");
            row.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at").HasColumnType("timestamp with time zone");
            row.Property(x => x.LeaseToken).HasColumnName("lease_token").HasColumnType("uuid");
            row.Property(x => x.DeadlineAt).HasColumnName("deadline_at").HasColumnType("timestamp with time zone");
            row.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
            row.Property(x => x.FinishedAt).HasColumnName("finished_at").HasColumnType("timestamp with time zone");
            row.Property(x => x.LastError).HasColumnName("last_error").HasColumnType("text");
            row.Property(x => x.Errors).HasColumnName("errors").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            row.Property(x => x.TraceParent).HasColumnName("trace_parent").HasColumnType("text");

            // The claim. Holds only unfinished messages, in claim order.
            row.HasIndex(x => new { x.Kind, x.Priority, x.NextAttemptAt })
                .IsDescending(false, true, false)
                .HasFilter("status = 'Pending'")
                .HasDatabaseName($"ix_{table}_claim");

            row.HasIndex(x => new { x.Kind, x.UniqueKey })
                .IsUnique()
                .HasFilter("unique_key IS NOT NULL")
                .HasDatabaseName($"ux_{table}_unique_key");

            // Operator lists and the parked gauge.
            row.HasIndex(x => new { x.Kind, x.FinishedAt })
                .HasFilter("status IN ('Parked', 'Rejected')")
                .HasDatabaseName($"ix_{table}_finished");
        });

        return modelBuilder;
    }
}
