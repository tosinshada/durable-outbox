using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DurableOutbox.Tests.Migrations.Contract
{
    /// <inheritdoc />
    public partial class ContractInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "contract");

            migrationBuilder.CreateTable(
                name: "messages",
                schema: "contract",
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
                    table.PrimaryKey("pk_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_messages_claim",
                schema: "contract",
                table: "messages",
                columns: new[] { "kind", "priority", "next_attempt_at" },
                descending: new[] { false, true, false },
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_messages_finished",
                schema: "contract",
                table: "messages",
                columns: new[] { "kind", "finished_at" },
                filter: "status IN ('Parked', 'Rejected')");

            migrationBuilder.CreateIndex(
                name: "ux_messages_unique_key",
                schema: "contract",
                table: "messages",
                columns: new[] { "kind", "unique_key" },
                unique: true,
                filter: "unique_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "messages",
                schema: "contract");
        }
    }
}
