using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Neo.AgentOrchestration.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableAgentRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AgentRunId",
                schema: "nao",
                table: "Deliveries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_AgentProfiles_Id_RoleProfileId_WorkspaceId_OrganizationId",
                schema: "nao",
                table: "AgentProfiles",
                columns: new[] { "Id", "RoleProfileId", "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateTable(
                name: "AgentRuns",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InitialWorkflowVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkItemVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreviousRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NextRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Hop = table.Column<int>(type: "int", nullable: false),
                    RequestedByAgentId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RequestedByChatId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Instructions = table.Column<string>(type: "nvarchar(max)", maxLength: 32000, nullable: true),
                    SkillPath = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Branch = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    SimulationOutcome = table.Column<byte>(type: "tinyint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    Decision = table.Column<byte>(type: "tinyint", nullable: false),
                    DecisionReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ResultSummary = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DispatchedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRuns", x => x.Id);
                    table.UniqueConstraint("AK_AgentRuns_Id_WorkItemId_ProjectId", x => new { x.Id, x.WorkItemId, x.ProjectId });
                    table.CheckConstraint("CK_AgentRuns_Hop", "[Hop] BETWEEN 1 AND 20 AND ([PreviousRunId] IS NULL OR [PreviousRunId] <> [Id])");
                    table.CheckConstraint("CK_AgentRuns_Status", "[Status] BETWEEN 1 AND 5 AND [Decision] BETWEEN 0 AND 4 AND [SimulationOutcome] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_AgentRuns_AgentProfiles_AgentProfileId_RoleId_WorkspaceId_OrganizationId",
                        columns: x => new { x.AgentProfileId, x.RoleId, x.WorkspaceId, x.OrganizationId },
                        principalSchema: "nao",
                        principalTable: "AgentProfiles",
                        principalColumns: new[] { "Id", "RoleProfileId", "WorkspaceId", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentRuns_AgentRuns_PreviousRunId_WorkItemId_ProjectId",
                        columns: x => new { x.PreviousRunId, x.WorkItemId, x.ProjectId },
                        principalSchema: "nao",
                        principalTable: "AgentRuns",
                        principalColumns: new[] { "Id", "WorkItemId", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentRuns_Projects_ProjectId_WorkspaceId_OrganizationId",
                        columns: x => new { x.ProjectId, x.WorkspaceId, x.OrganizationId },
                        principalSchema: "nao",
                        principalTable: "Projects",
                        principalColumns: new[] { "Id", "WorkspaceId", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentRuns_WorkItems_WorkItemId_ProjectId",
                        columns: x => new { x.WorkItemId, x.ProjectId },
                        principalSchema: "nao",
                        principalTable: "WorkItems",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentRuns_WorkflowDefinitions_WorkflowId_ProjectId",
                        columns: x => new { x.WorkflowId, x.ProjectId },
                        principalSchema: "nao",
                        principalTable: "WorkflowDefinitions",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_AgentRunId_WorkItemId_ProjectId",
                schema: "nao",
                table: "Deliveries",
                columns: new[] { "AgentRunId", "WorkItemId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_AgentProfileId_RoleId_WorkspaceId_OrganizationId",
                schema: "nao",
                table: "AgentRuns",
                columns: new[] { "AgentProfileId", "RoleId", "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_PreviousRunId",
                schema: "nao",
                table: "AgentRuns",
                column: "PreviousRunId",
                unique: true,
                filter: "[PreviousRunId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_PreviousRunId_WorkItemId_ProjectId",
                schema: "nao",
                table: "AgentRuns",
                columns: new[] { "PreviousRunId", "WorkItemId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_ProjectId_WorkspaceId_OrganizationId",
                schema: "nao",
                table: "AgentRuns",
                columns: new[] { "ProjectId", "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_WorkflowId_ProjectId",
                schema: "nao",
                table: "AgentRuns",
                columns: new[] { "WorkflowId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_WorkItemId",
                schema: "nao",
                table: "AgentRuns",
                column: "WorkItemId",
                unique: true,
                filter: "[Status] IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_WorkItemId_ProjectId",
                schema: "nao",
                table: "AgentRuns",
                columns: new[] { "WorkItemId", "ProjectId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Deliveries_AgentRuns_AgentRunId_WorkItemId_ProjectId",
                schema: "nao",
                table: "Deliveries",
                columns: new[] { "AgentRunId", "WorkItemId", "ProjectId" },
                principalSchema: "nao",
                principalTable: "AgentRuns",
                principalColumns: new[] { "Id", "WorkItemId", "ProjectId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Deliveries_AgentRuns_AgentRunId_WorkItemId_ProjectId",
                schema: "nao",
                table: "Deliveries");

            migrationBuilder.DropTable(
                name: "AgentRuns",
                schema: "nao");

            migrationBuilder.DropIndex(
                name: "IX_Deliveries_AgentRunId_WorkItemId_ProjectId",
                schema: "nao",
                table: "Deliveries");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AgentProfiles_Id_RoleProfileId_WorkspaceId_OrganizationId",
                schema: "nao",
                table: "AgentProfiles");

            migrationBuilder.DropColumn(
                name: "AgentRunId",
                schema: "nao",
                table: "Deliveries");
        }
    }
}
