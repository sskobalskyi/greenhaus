using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Greenhaus.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "greenhaus");

            migrationBuilder.CreateTable(
                name: "entitlements",
                schema: "greenhaus",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Tier = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    ValidUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entitlements", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "greenhaus",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pots",
                schema: "greenhaus",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Location = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ViewMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    GridRows = table.Column<int>(type: "integer", nullable: true),
                    GridCols = table.Column<int>(type: "integer", nullable: true),
                    KillSwitch = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "profiles",
                schema: "greenhaus",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    Locale = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "actuators",
                schema: "greenhaus",
                columns: table => new
                {
                    PotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<string>(type: "text", nullable: false),
                    DeviceId = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MaxRunSeconds = table.Column<int>(type: "integer", nullable: false),
                    MinIntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    DailyCapSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxFruitlessRuns = table.Column<int>(type: "integer", nullable: true),
                    HardMaxRunSeconds = table.Column<int>(type: "integer", nullable: false),
                    HardMinIntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    HardDailyCapSeconds = table.Column<int>(type: "integer", nullable: false),
                    LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_actuators", x => new { x.PotId, x.Id });
                    table.ForeignKey(
                        name: "FK_actuators_pots_PotId",
                        column: x => x.PotId,
                        principalSchema: "greenhaus",
                        principalTable: "pots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                schema: "greenhaus",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PotId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    ActuatorId = table.Column<string>(type: "text", nullable: true),
                    Action = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    Outcome = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.Id);
                    table.ForeignKey(
                        name: "FK_audit_log_pots_PotId",
                        column: x => x.PotId,
                        principalSchema: "greenhaus",
                        principalTable: "pots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "care_tasks",
                schema: "greenhaus",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Origin = table.Column<string>(type: "text", nullable: true),
                    RepeatEveryDays = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_care_tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_care_tasks_pots_PotId",
                        column: x => x.PotId,
                        principalSchema: "greenhaus",
                        principalTable: "pots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "channels",
                schema: "greenhaus",
                columns: table => new
                {
                    DeviceId = table.Column<string>(type: "text", nullable: false),
                    ChannelId = table.Column<string>(type: "text", nullable: false),
                    PotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Metric = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Unit = table.Column<string>(type: "text", nullable: true),
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    DryRaw = table.Column<double>(type: "double precision", nullable: true),
                    WetRaw = table.Column<double>(type: "double precision", nullable: true),
                    CalibratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channels", x => new { x.DeviceId, x.ChannelId });
                    table.ForeignKey(
                        name: "FK_channels_pots_PotId",
                        column: x => x.PotId,
                        principalSchema: "greenhaus",
                        principalTable: "pots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "linked_devices",
                schema: "greenhaus",
                columns: table => new
                {
                    DeviceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PotId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    Model = table.Column<string>(type: "text", nullable: false),
                    ThermalCutoffDeclared = table.Column<bool>(type: "boolean", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_linked_devices", x => x.DeviceId);
                    table.ForeignKey(
                        name: "FK_linked_devices_pots_PotId",
                        column: x => x.PotId,
                        principalSchema: "greenhaus",
                        principalTable: "pots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plants",
                schema: "greenhaus",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpeciesId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Nickname = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlantedAt = table.Column<DateOnly>(type: "date", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_plants_pots_PotId",
                        column: x => x.PotId,
                        principalSchema: "greenhaus",
                        principalTable: "pots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "requirements",
                schema: "greenhaus",
                columns: table => new
                {
                    PotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Metric = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Min = table.Column<double>(type: "double precision", nullable: true),
                    Max = table.Column<double>(type: "double precision", nullable: true),
                    Margin = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requirements", x => new { x.PotId, x.Metric });
                    table.ForeignKey(
                        name: "FK_requirements_pots_PotId",
                        column: x => x.PotId,
                        principalSchema: "greenhaus",
                        principalTable: "pots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "zones",
                schema: "greenhaus",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Row = table.Column<int>(type: "integer", nullable: false),
                    Col = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_zones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_zones_pots_PotId",
                        column: x => x.PotId,
                        principalSchema: "greenhaus",
                        principalTable: "pots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_PotId_At",
                schema: "greenhaus",
                table: "audit_log",
                columns: new[] { "PotId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_care_tasks_PotId_State",
                schema: "greenhaus",
                table: "care_tasks",
                columns: new[] { "PotId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_channels_PotId",
                schema: "greenhaus",
                table: "channels",
                column: "PotId");

            migrationBuilder.CreateIndex(
                name: "IX_linked_devices_OwnerId",
                schema: "greenhaus",
                table: "linked_devices",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_linked_devices_PotId",
                schema: "greenhaus",
                table: "linked_devices",
                column: "PotId");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_PublishedAt",
                schema: "greenhaus",
                table: "outbox",
                column: "PublishedAt");

            migrationBuilder.CreateIndex(
                name: "IX_plants_PotId",
                schema: "greenhaus",
                table: "plants",
                column: "PotId");

            migrationBuilder.CreateIndex(
                name: "IX_pots_OwnerId",
                schema: "greenhaus",
                table: "pots",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_zones_PotId",
                schema: "greenhaus",
                table: "zones",
                column: "PotId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "actuators",
                schema: "greenhaus");

            migrationBuilder.DropTable(
                name: "audit_log",
                schema: "greenhaus");

            migrationBuilder.DropTable(
                name: "care_tasks",
                schema: "greenhaus");

            migrationBuilder.DropTable(
                name: "channels",
                schema: "greenhaus");

            migrationBuilder.DropTable(
                name: "entitlements",
                schema: "greenhaus");

            migrationBuilder.DropTable(
                name: "linked_devices",
                schema: "greenhaus");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "greenhaus");

            migrationBuilder.DropTable(
                name: "plants",
                schema: "greenhaus");

            migrationBuilder.DropTable(
                name: "profiles",
                schema: "greenhaus");

            migrationBuilder.DropTable(
                name: "requirements",
                schema: "greenhaus");

            migrationBuilder.DropTable(
                name: "zones",
                schema: "greenhaus");

            migrationBuilder.DropTable(
                name: "pots",
                schema: "greenhaus");
        }
    }
}
