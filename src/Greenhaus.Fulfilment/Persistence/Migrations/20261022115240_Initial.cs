using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Greenhaus.Fulfilment.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "fulfilment");

            migrationBuilder.CreateTable(
                name: "allocations",
                schema: "fulfilment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Index = table.Column<int>(type: "integer", nullable: false),
                    DeviceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Intent = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    ClaimToken = table.Column<string>(type: "text", nullable: true),
                    Credential = table.Column<string>(type: "text", nullable: true),
                    ClaimTokenHash = table.Column<string>(type: "text", nullable: false),
                    ClaimTokenExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CredentialHash = table.Column<string>(type: "text", nullable: false),
                    Capabilities = table.Column<string>(type: "jsonb", nullable: false),
                    AllocatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ShippedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PulseAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_allocations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "fulfilment",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Consumer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox", x => new { x.MessageId, x.Consumer });
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "fulfilment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Traceparent = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_allocations_DeviceId",
                schema: "fulfilment",
                table: "allocations",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_allocations_OrderId_Sku_Index",
                schema: "fulfilment",
                table: "allocations",
                columns: new[] { "OrderId", "Sku", "Index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_allocations_State_NextAttemptAt",
                schema: "fulfilment",
                table: "allocations",
                columns: new[] { "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_PublishedAt",
                schema: "fulfilment",
                table: "outbox",
                column: "PublishedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "allocations",
                schema: "fulfilment");

            migrationBuilder.DropTable(
                name: "inbox",
                schema: "fulfilment");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "fulfilment");
        }
    }
}
