using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Greenhaus.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Dunning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextRetryAt",
                schema: "billing",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PastDueSince",
                schema: "billing",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetryCount",
                schema: "billing",
                table: "subscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NextRetryAt",
                schema: "billing",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "PastDueSince",
                schema: "billing",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "RetryCount",
                schema: "billing",
                table: "subscriptions");
        }
    }
}
