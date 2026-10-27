using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Greenhaus.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssistantQuotaReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Reserved",
                schema: "greenhaus",
                table: "assistant_usage",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReservedAt",
                schema: "greenhaus",
                table: "assistant_usage",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Reserved",
                schema: "greenhaus",
                table: "assistant_usage");

            migrationBuilder.DropColumn(
                name: "ReservedAt",
                schema: "greenhaus",
                table: "assistant_usage");
        }
    }
}
