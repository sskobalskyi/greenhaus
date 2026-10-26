using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Greenhaus.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutomationLoop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BlockedReason",
                schema: "greenhaus",
                table: "actuators",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "CounterDay",
                schema: "greenhaus",
                table: "actuators",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FruitlessRuns",
                schema: "greenhaus",
                table: "actuators",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastNoteCode",
                schema: "greenhaus",
                table: "actuators",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastRunSeconds",
                schema: "greenhaus",
                table: "actuators",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "LastRunStartValue",
                schema: "greenhaus",
                table: "actuators",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RunSecondsToday",
                schema: "greenhaus",
                table: "actuators",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RunningUntil",
                schema: "greenhaus",
                table: "actuators",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "State",
                schema: "greenhaus",
                table: "actuators",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SuggestedAt",
                schema: "greenhaus",
                table: "actuators",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlockedReason",
                schema: "greenhaus",
                table: "actuators");

            migrationBuilder.DropColumn(
                name: "CounterDay",
                schema: "greenhaus",
                table: "actuators");

            migrationBuilder.DropColumn(
                name: "FruitlessRuns",
                schema: "greenhaus",
                table: "actuators");

            migrationBuilder.DropColumn(
                name: "LastNoteCode",
                schema: "greenhaus",
                table: "actuators");

            migrationBuilder.DropColumn(
                name: "LastRunSeconds",
                schema: "greenhaus",
                table: "actuators");

            migrationBuilder.DropColumn(
                name: "LastRunStartValue",
                schema: "greenhaus",
                table: "actuators");

            migrationBuilder.DropColumn(
                name: "RunSecondsToday",
                schema: "greenhaus",
                table: "actuators");

            migrationBuilder.DropColumn(
                name: "RunningUntil",
                schema: "greenhaus",
                table: "actuators");

            migrationBuilder.DropColumn(
                name: "State",
                schema: "greenhaus",
                table: "actuators");

            migrationBuilder.DropColumn(
                name: "SuggestedAt",
                schema: "greenhaus",
                table: "actuators");
        }
    }
}
