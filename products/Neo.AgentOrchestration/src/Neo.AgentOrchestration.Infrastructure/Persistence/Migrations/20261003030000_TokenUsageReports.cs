using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Neo.AgentOrchestration.Infrastructure.Persistence.Migrations;

public partial class TokenUsageReports : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddUniqueConstraint(
            name: "AK_AgentRuns_Id_WorkItemId", schema: "nao", table: "AgentRuns",
            columns: new[] { "Id", "WorkItemId" });
        migrationBuilder.CreateTable(
            name: "TokenUsageReports", schema: "nao",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                WorkItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AgentRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Provider = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                Model = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                InputTokens = table.Column<long>(type: "bigint", nullable: true),
                OutputTokens = table.Column<long>(type: "bigint", nullable: true),
                CachedInputTokens = table.Column<long>(type: "bigint", nullable: true),
                ReasoningTokens = table.Column<long>(type: "bigint", nullable: true),
                Source = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                IdempotencyKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            }, constraints: table =>
            {
                table.PrimaryKey("PK_TokenUsageReports", x => x.Id);
                table.UniqueConstraint("AK_TokenUsageReports_Id_WorkItemId_AgentRunId", x => new { x.Id, x.WorkItemId, x.AgentRunId });
                table.ForeignKey("FK_TokenUsageReports_AgentRuns_AgentRunId_WorkItemId", x => new { x.AgentRunId, x.WorkItemId },
                    principalSchema: "nao", principalTable: "AgentRuns", principalColumns: new[] { "Id", "WorkItemId" }, onDelete: ReferentialAction.Restrict);
                table.CheckConstraint("CK_TokenUsageReports_Counts", "([InputTokens] IS NULL OR [InputTokens] >= 0) AND ([OutputTokens] IS NULL OR [OutputTokens] >= 0) AND ([CachedInputTokens] IS NULL OR [CachedInputTokens] >= 0) AND ([ReasoningTokens] IS NULL OR [ReasoningTokens] >= 0)");
            });
        migrationBuilder.CreateIndex("IX_TokenUsageReports_OrganizationId_WorkspaceId_AgentRunId_RecordedAtUtc", "TokenUsageReports", new[] { "OrganizationId", "WorkspaceId", "AgentRunId", "RecordedAtUtc" }, schema: "nao");
        migrationBuilder.CreateIndex("IX_TokenUsageReports_OrganizationId_WorkspaceId_IdempotencyKey", "TokenUsageReports", new[] { "OrganizationId", "WorkspaceId", "IdempotencyKey" }, schema: "nao", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "TokenUsageReports", schema: "nao");
        migrationBuilder.DropUniqueConstraint(name: "AK_AgentRuns_Id_WorkItemId", schema: "nao", table: "AgentRuns");
    }
}
