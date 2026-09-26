using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Neo.AgentOrchestration.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialOrchestration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "nao");

            migrationBuilder.CreateTable(
                name: "Organizations",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organizations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Workspaces",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.Id);
                    table.UniqueConstraint("AK_Workspaces_Id_OrganizationId", x => new { x.Id, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_Workspaces_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "nao",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.UniqueConstraint("AK_Projects_Id_WorkspaceId_OrganizationId", x => new { x.Id, x.WorkspaceId, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_Projects_Workspaces_WorkspaceId_OrganizationId",
                        columns: x => new { x.WorkspaceId, x.OrganizationId },
                        principalSchema: "nao",
                        principalTable: "Workspaces",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RoleProfiles",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ScopeDescription = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleProfiles", x => x.Id);
                    table.UniqueConstraint("AK_RoleProfiles_Id_WorkspaceId_OrganizationId", x => new { x.Id, x.WorkspaceId, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_RoleProfiles_Workspaces_WorkspaceId_OrganizationId",
                        columns: x => new { x.WorkspaceId, x.OrganizationId },
                        principalSchema: "nao",
                        principalTable: "Workspaces",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowDefinitions",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowDefinitions", x => x.Id);
                    table.UniqueConstraint("AK_WorkflowDefinitions_Id_ProjectId", x => new { x.Id, x.ProjectId });
                    table.UniqueConstraint("AK_WorkflowDefinitions_Id_WorkspaceId_OrganizationId", x => new { x.Id, x.WorkspaceId, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_WorkflowDefinitions_Projects_ProjectId_WorkspaceId_OrganizationId",
                        columns: x => new { x.ProjectId, x.WorkspaceId, x.OrganizationId },
                        principalSchema: "nao",
                        principalTable: "Projects",
                        principalColumns: new[] { "Id", "WorkspaceId", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AgentProfiles",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Instructions = table.Column<string>(type: "nvarchar(max)", maxLength: 32000, nullable: true),
                    SkillPath = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentProfiles_RoleProfiles_RoleProfileId_WorkspaceId_OrganizationId",
                        columns: x => new { x.RoleProfileId, x.WorkspaceId, x.OrganizationId },
                        principalSchema: "nao",
                        principalTable: "RoleProfiles",
                        principalColumns: new[] { "Id", "WorkspaceId", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkItems",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentWorkItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Key = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Domain = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", maxLength: 32000, nullable: true),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    Priority = table.Column<byte>(type: "tinyint", nullable: false),
                    OwnerRoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerAgentId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OwnerChatId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Branch = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ArchivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    EstimatedSeconds = table.Column<long>(type: "bigint", nullable: true),
                    Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItems", x => x.Id);
                    table.UniqueConstraint("AK_WorkItems_Id_ProjectId", x => new { x.Id, x.ProjectId });
                    table.CheckConstraint("CK_WorkItems_Estimate", "[EstimatedSeconds] IS NULL OR [EstimatedSeconds] > 0");
                    table.CheckConstraint("CK_WorkItems_Owner", "([OwnerRoleId] IS NULL AND [OwnerAgentId] IS NULL AND [OwnerChatId] IS NULL AND [Status] <> 3) OR ([OwnerRoleId] IS NOT NULL AND [OwnerAgentId] IS NOT NULL AND [OwnerChatId] IS NOT NULL)");
                    table.CheckConstraint("CK_WorkItems_Parent", "[ParentWorkItemId] IS NULL OR [ParentWorkItemId] <> [Id]");
                    table.CheckConstraint("CK_WorkItems_Status", "[Status] BETWEEN 1 AND 7 AND [Priority] BETWEEN 1 AND 4");
                    table.ForeignKey(
                        name: "FK_WorkItems_Projects_ProjectId_WorkspaceId_OrganizationId",
                        columns: x => new { x.ProjectId, x.WorkspaceId, x.OrganizationId },
                        principalSchema: "nao",
                        principalTable: "Projects",
                        principalColumns: new[] { "Id", "WorkspaceId", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkItems_RoleProfiles_OwnerRoleId_WorkspaceId_OrganizationId",
                        columns: x => new { x.OwnerRoleId, x.WorkspaceId, x.OrganizationId },
                        principalSchema: "nao",
                        principalTable: "RoleProfiles",
                        principalColumns: new[] { "Id", "WorkspaceId", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkItems_WorkItems_ParentWorkItemId_ProjectId",
                        columns: x => new { x.ParentWorkItemId, x.ProjectId },
                        principalSchema: "nao",
                        principalTable: "WorkItems",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowTransitions",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    FromRoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromStatus = table.Column<byte>(type: "tinyint", nullable: false),
                    ToRoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ToStatus = table.Column<byte>(type: "tinyint", nullable: false),
                    RequireCommit = table.Column<bool>(type: "bit", nullable: false),
                    RequirePassingTests = table.Column<bool>(type: "bit", nullable: false),
                    RequireApproval = table.Column<bool>(type: "bit", nullable: false),
                    RequiredArtifact = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowTransitions", x => x.Id);
                    table.UniqueConstraint("AK_WorkflowTransitions_Id_WorkflowDefinitionId", x => new { x.Id, x.WorkflowDefinitionId });
                    table.ForeignKey(
                        name: "FK_WorkflowTransitions_RoleProfiles_FromRoleId_WorkspaceId_OrganizationId",
                        columns: x => new { x.FromRoleId, x.WorkspaceId, x.OrganizationId },
                        principalSchema: "nao",
                        principalTable: "RoleProfiles",
                        principalColumns: new[] { "Id", "WorkspaceId", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowTransitions_RoleProfiles_ToRoleId_WorkspaceId_OrganizationId",
                        columns: x => new { x.ToRoleId, x.WorkspaceId, x.OrganizationId },
                        principalSchema: "nao",
                        principalTable: "RoleProfiles",
                        principalColumns: new[] { "Id", "WorkspaceId", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowTransitions_WorkflowDefinitions_WorkflowDefinitionId_WorkspaceId_OrganizationId",
                        columns: x => new { x.WorkflowDefinitionId, x.WorkspaceId, x.OrganizationId },
                        principalSchema: "nao",
                        principalTable: "WorkflowDefinitions",
                        principalColumns: new[] { "Id", "WorkspaceId", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkItemDependencies",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DependsOnWorkItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemDependencies", x => x.Id);
                    table.CheckConstraint("CK_Dependency_Self", "[WorkItemId] <> [DependsOnWorkItemId]");
                    table.ForeignKey(
                        name: "FK_WorkItemDependencies_WorkItems_DependsOnWorkItemId_ProjectId",
                        columns: x => new { x.DependsOnWorkItemId, x.ProjectId },
                        principalSchema: "nao",
                        principalTable: "WorkItems",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkItemDependencies_WorkItems_WorkItemId_ProjectId",
                        columns: x => new { x.WorkItemId, x.ProjectId },
                        principalSchema: "nao",
                        principalTable: "WorkItems",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkItemEvidence",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    CommitSha = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Outcome = table.Column<byte>(type: "tinyint", nullable: false),
                    Details = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkItemEvidence_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalSchema: "nao",
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkItemLogs",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ChatId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkItemLogs_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalSchema: "nao",
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkItemTimeEntries",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DurationSeconds = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemTimeEntries", x => x.Id);
                    table.CheckConstraint("CK_Time_Duration", "[DurationSeconds] >= 0 AND ([EndedAtUtc] IS NULL OR [EndedAtUtc] >= [StartedAtUtc])");
                    table.ForeignKey(
                        name: "FK_WorkItemTimeEntries_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalSchema: "nao",
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowApprovals",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkItemVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TransitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewerAgentId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ReviewerChatId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Approved = table.Column<bool>(type: "bit", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowApprovals_WorkItems_WorkItemId_ProjectId",
                        columns: x => new { x.WorkItemId, x.ProjectId },
                        principalSchema: "nao",
                        principalTable: "WorkItems",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowApprovals_WorkflowDefinitions_WorkflowDefinitionId_ProjectId",
                        columns: x => new { x.WorkflowDefinitionId, x.ProjectId },
                        principalSchema: "nao",
                        principalTable: "WorkflowDefinitions",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowApprovals_WorkflowTransitions_TransitionId_WorkflowDefinitionId",
                        columns: x => new { x.TransitionId, x.WorkflowDefinitionId },
                        principalSchema: "nao",
                        principalTable: "WorkflowTransitions",
                        principalColumns: new[] { "Id", "WorkflowDefinitionId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentProfiles_RoleProfileId_WorkspaceId_OrganizationId",
                schema: "nao",
                table: "AgentProfiles",
                columns: new[] { "RoleProfileId", "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentProfiles_WorkspaceId_Key",
                schema: "nao",
                table: "AgentProfiles",
                columns: new[] { "WorkspaceId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_Key",
                schema: "nao",
                table: "Organizations",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_WorkspaceId_Key",
                schema: "nao",
                table: "Projects",
                columns: new[] { "WorkspaceId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_WorkspaceId_OrganizationId",
                schema: "nao",
                table: "Projects",
                columns: new[] { "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_RoleProfiles_WorkspaceId_Key",
                schema: "nao",
                table: "RoleProfiles",
                columns: new[] { "WorkspaceId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoleProfiles_WorkspaceId_OrganizationId",
                schema: "nao",
                table: "RoleProfiles",
                columns: new[] { "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowApprovals_TransitionId_WorkflowDefinitionId",
                schema: "nao",
                table: "WorkflowApprovals",
                columns: new[] { "TransitionId", "WorkflowDefinitionId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowApprovals_WorkflowDefinitionId_ProjectId",
                schema: "nao",
                table: "WorkflowApprovals",
                columns: new[] { "WorkflowDefinitionId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowApprovals_WorkItemId_ProjectId",
                schema: "nao",
                table: "WorkflowApprovals",
                columns: new[] { "WorkItemId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowApprovals_WorkItemId_WorkItemVersion_WorkflowVersion",
                schema: "nao",
                table: "WorkflowApprovals",
                columns: new[] { "WorkItemId", "WorkItemVersion", "WorkflowVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinitions_ProjectId_Key",
                schema: "nao",
                table: "WorkflowDefinitions",
                columns: new[] { "ProjectId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowDefinitions_ProjectId_WorkspaceId_OrganizationId",
                schema: "nao",
                table: "WorkflowDefinitions",
                columns: new[] { "ProjectId", "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTransitions_FromRoleId_WorkspaceId_OrganizationId",
                schema: "nao",
                table: "WorkflowTransitions",
                columns: new[] { "FromRoleId", "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTransitions_ToRoleId_WorkspaceId_OrganizationId",
                schema: "nao",
                table: "WorkflowTransitions",
                columns: new[] { "ToRoleId", "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTransitions_WorkflowDefinitionId_FromRoleId_FromStatus",
                schema: "nao",
                table: "WorkflowTransitions",
                columns: new[] { "WorkflowDefinitionId", "FromRoleId", "FromStatus" },
                unique: true,
                filter: "[IsEnabled] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTransitions_WorkflowDefinitionId_Key",
                schema: "nao",
                table: "WorkflowTransitions",
                columns: new[] { "WorkflowDefinitionId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTransitions_WorkflowDefinitionId_WorkspaceId_OrganizationId",
                schema: "nao",
                table: "WorkflowTransitions",
                columns: new[] { "WorkflowDefinitionId", "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemDependencies_DependsOnWorkItemId_ProjectId",
                schema: "nao",
                table: "WorkItemDependencies",
                columns: new[] { "DependsOnWorkItemId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemDependencies_WorkItemId_DependsOnWorkItemId",
                schema: "nao",
                table: "WorkItemDependencies",
                columns: new[] { "WorkItemId", "DependsOnWorkItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemDependencies_WorkItemId_ProjectId",
                schema: "nao",
                table: "WorkItemDependencies",
                columns: new[] { "WorkItemId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemEvidence_WorkItemId_Sequence",
                schema: "nao",
                table: "WorkItemEvidence",
                columns: new[] { "WorkItemId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemLogs_WorkItemId_CreatedAtUtc",
                schema: "nao",
                table: "WorkItemLogs",
                columns: new[] { "WorkItemId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_OwnerRoleId",
                schema: "nao",
                table: "WorkItems",
                column: "OwnerRoleId",
                unique: true,
                filter: "[Status] = 3 AND [OwnerRoleId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_OwnerRoleId_WorkspaceId_OrganizationId",
                schema: "nao",
                table: "WorkItems",
                columns: new[] { "OwnerRoleId", "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_ParentWorkItemId_ProjectId",
                schema: "nao",
                table: "WorkItems",
                columns: new[] { "ParentWorkItemId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_ProjectId_Key",
                schema: "nao",
                table: "WorkItems",
                columns: new[] { "ProjectId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_ProjectId_WorkspaceId_OrganizationId",
                schema: "nao",
                table: "WorkItems",
                columns: new[] { "ProjectId", "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_WorkspaceId_ProjectId_Domain_Status",
                schema: "nao",
                table: "WorkItems",
                columns: new[] { "WorkspaceId", "ProjectId", "Domain", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemTimeEntries_WorkItemId",
                schema: "nao",
                table: "WorkItemTimeEntries",
                column: "WorkItemId",
                unique: true,
                filter: "[EndedAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_OrganizationId_Key",
                schema: "nao",
                table: "Workspaces",
                columns: new[] { "OrganizationId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentProfiles",
                schema: "nao");

            migrationBuilder.DropTable(
                name: "WorkflowApprovals",
                schema: "nao");

            migrationBuilder.DropTable(
                name: "WorkItemDependencies",
                schema: "nao");

            migrationBuilder.DropTable(
                name: "WorkItemEvidence",
                schema: "nao");

            migrationBuilder.DropTable(
                name: "WorkItemLogs",
                schema: "nao");

            migrationBuilder.DropTable(
                name: "WorkItemTimeEntries",
                schema: "nao");

            migrationBuilder.DropTable(
                name: "WorkflowTransitions",
                schema: "nao");

            migrationBuilder.DropTable(
                name: "WorkItems",
                schema: "nao");

            migrationBuilder.DropTable(
                name: "WorkflowDefinitions",
                schema: "nao");

            migrationBuilder.DropTable(
                name: "RoleProfiles",
                schema: "nao");

            migrationBuilder.DropTable(
                name: "Projects",
                schema: "nao");

            migrationBuilder.DropTable(
                name: "Workspaces",
                schema: "nao");

            migrationBuilder.DropTable(
                name: "Organizations",
                schema: "nao");
        }
    }
}
