using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DurableOutbox.Tests.Migrations
{
    /// <inheritdoc />
    public partial class AddOperatorIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_outbox_finished",
                schema: "svc",
                table: "outbox",
                columns: new[] { "kind", "finished_at" },
                filter: "status IN ('Parked', 'Rejected')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_finished",
                schema: "svc",
                table: "outbox");
        }
    }
}
