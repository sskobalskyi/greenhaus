using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Greenhaus.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssistantUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assistant_usage",
                schema: "greenhaus",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_usage", x => new { x.UserId, x.Month });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assistant_usage",
                schema: "greenhaus");
        }
    }
}
