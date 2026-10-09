using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Neo.AgentOrchestration.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkItemTokenMetering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "EstimatedTokens",
                schema: "nao",
                table: "WorkItems",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkTokenUsage",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ChatId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Reference = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: true),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: true),
                    CachedInputTokens = table.Column<long>(type: "bigint", nullable: true),
                    ReasoningTokens = table.Column<long>(type: "bigint", nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkTokenUsage", x => x.Id);
                    table.CheckConstraint("CK_WorkTokenUsage_Counts", "([InputTokens] IS NULL OR [InputTokens] >= 0) AND ([OutputTokens] IS NULL OR [OutputTokens] >= 0) AND ([CachedInputTokens] IS NULL OR [CachedInputTokens] >= 0) AND ([ReasoningTokens] IS NULL OR [ReasoningTokens] >= 0) AND ([InputTokens] IS NULL OR [CachedInputTokens] IS NULL OR [CachedInputTokens] <= [InputTokens]) AND ([OutputTokens] IS NULL OR [ReasoningTokens] IS NULL OR [ReasoningTokens] <= [OutputTokens]) AND ([InputTokens] IS NOT NULL OR [OutputTokens] IS NOT NULL OR [CachedInputTokens] IS NOT NULL OR [ReasoningTokens] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_WorkTokenUsage_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalSchema: "nao",
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkItems_TokenEstimate",
                schema: "nao",
                table: "WorkItems",
                sql: "[EstimatedTokens] IS NULL OR [EstimatedTokens] > 0");

            migrationBuilder.CreateIndex(
                name: "IX_WorkTokenUsage_WorkItemId_RecordedAtUtc",
                schema: "nao",
                table: "WorkTokenUsage",
                columns: new[] { "WorkItemId", "RecordedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkTokenUsage",
                schema: "nao");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkItems_TokenEstimate",
                schema: "nao",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "EstimatedTokens",
                schema: "nao",
                table: "WorkItems");
        }
    }
}
