using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Greenhaus.Billing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GrantAtRiskReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AtRiskReason",
                schema: "billing",
                table: "grants",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AtRiskReason",
                schema: "billing",
                table: "grants");
        }
    }
}
