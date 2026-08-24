using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OhMyBot.Plugins.QqApproval.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QqApprovalListenerSettings",
                columns: table => new
                {
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    RulesEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QqApprovalListenerSettings", x => x.Kind);
                });

            migrationBuilder.CreateTable(
                name: "QqApprovalRequests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Flag = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    BotInstanceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequesterId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequesterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    GroupId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Comment = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    RequesterProfileJson = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false, defaultValue: ""),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DecidedByCoreUserId = table.Column<long>(type: "bigint", nullable: true),
                    DecidedReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QqApprovalRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QqApprovalRules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Action = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QqApprovalRules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QqApprovalRequests_Flag",
                table: "QqApprovalRequests",
                column: "Flag",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QqApprovalRequests_Status_CreatedAt",
                table: "QqApprovalRequests",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_QqApprovalRules_Kind_Scope_Value",
                table: "QqApprovalRules",
                columns: new[] { "Kind", "Scope", "Value" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QqApprovalListenerSettings");

            migrationBuilder.DropTable(
                name: "QqApprovalRequests");

            migrationBuilder.DropTable(
                name: "QqApprovalRules");
        }
    }
}
