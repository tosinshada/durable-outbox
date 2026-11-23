using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DurableOutbox.Tests.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "svc");

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "svc",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "svc",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    record_ref = table.Column<string>(type: "text", nullable: false),
                    unique_key = table.Column<string>(type: "text", nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    deadline_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    errors = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    trace_parent = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_claim",
                schema: "svc",
                table: "outbox",
                columns: new[] { "kind", "priority", "next_attempt_at" },
                descending: new[] { false, true, false },
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ux_outbox_unique_key",
                schema: "svc",
                table: "outbox",
                columns: new[] { "kind", "unique_key" },
                unique: true,
                filter: "unique_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "orders",
                schema: "svc");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "svc");
        }
    }
}
