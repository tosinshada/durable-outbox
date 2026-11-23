using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace DurableOutbox;

/// <summary>
/// Builds the statements once per <typeparamref name="TContext"/>, from the schema and table name in
/// its EF Core model, so the SQL always matches the service's migration.
/// </summary>
internal sealed class OutboxSqlProvider<TContext> where TContext : DbContext
{
    private OutboxSql? _sql;

    public OutboxSql Get(TContext context) => _sql ??= Build(context);

    private static OutboxSql Build(TContext context)
    {
        var entity = context.Model.FindEntityType(typeof(OutboxMessageRow))
            ?? throw new InvalidOperationException(
                $"{typeof(TContext).Name} has no outbox table. Call modelBuilder.AddDurableOutbox(...) in OnModelCreating.");

        var table = context.GetService<ISqlGenerationHelper>()
            .DelimitIdentifier(entity.GetTableName()!, entity.GetSchema());

        return new OutboxSql(table);
    }
}
