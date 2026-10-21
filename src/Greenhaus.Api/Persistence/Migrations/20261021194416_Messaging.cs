using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Greenhaus.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Messaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                schema: "greenhaus",
                table: "outbox",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Traceparent",
                schema: "greenhaus",
                table: "outbox",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CommandId",
                schema: "greenhaus",
                table: "audit_log",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "device_orders",
                schema: "greenhaus",
                columns: table => new
                {
                    DeviceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<string>(type: "text", nullable: false),
                    AllocatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_orders", x => x.DeviceId);
                });

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "greenhaus",
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

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_CommandId",
                schema: "greenhaus",
                table: "audit_log",
                column: "CommandId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_orders",
                schema: "greenhaus");

            migrationBuilder.DropTable(
                name: "inbox",
                schema: "greenhaus");

            migrationBuilder.DropIndex(
                name: "IX_audit_log_CommandId",
                schema: "greenhaus",
                table: "audit_log");

            migrationBuilder.DropColumn(
                name: "Attempts",
                schema: "greenhaus",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "Traceparent",
                schema: "greenhaus",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "CommandId",
                schema: "greenhaus",
                table: "audit_log");
        }
    }
}
