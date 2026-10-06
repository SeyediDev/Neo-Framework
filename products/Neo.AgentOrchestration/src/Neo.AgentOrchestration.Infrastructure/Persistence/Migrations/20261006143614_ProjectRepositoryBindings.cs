using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Neo.AgentOrchestration.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProjectRepositoryBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RepositoryBindingId",
                schema: "nao",
                table: "AgentRuns",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RepositoryDefaultBranch",
                schema: "nao",
                table: "AgentRuns",
                type: "nvarchar(250)",
                maxLength: 250,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RepositoryDevelopmentBranch",
                schema: "nao",
                table: "AgentRuns",
                type: "nvarchar(250)",
                maxLength: 250,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RepositoryKey",
                schema: "nao",
                table: "AgentRuns",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RepositoryUrl",
                schema: "nao",
                table: "AgentRuns",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProjectRepositoryBindings",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    RepositoryUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    RepositoryKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DefaultBranch = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    DevelopmentBranch = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    CiCdReference = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SecretReference = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectRepositoryBindings", x => x.Id);
                    table.UniqueConstraint("AK_ProjectRepositoryBindings_Id_ProjectId", x => new { x.Id, x.ProjectId });
                    table.ForeignKey(
                        name: "FK_ProjectRepositoryBindings_Projects_ProjectId_WorkspaceId_OrganizationId",
                        columns: x => new { x.ProjectId, x.WorkspaceId, x.OrganizationId },
                        principalSchema: "nao",
                        principalTable: "Projects",
                        principalColumns: new[] { "Id", "WorkspaceId", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectRepositoryBindings_ProjectId",
                schema: "nao",
                table: "ProjectRepositoryBindings",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectRepositoryBindings_ProjectId_WorkspaceId_OrganizationId",
                schema: "nao",
                table: "ProjectRepositoryBindings",
                columns: new[] { "ProjectId", "WorkspaceId", "OrganizationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectRepositoryBindings",
                schema: "nao");

            migrationBuilder.DropColumn(
                name: "RepositoryBindingId",
                schema: "nao",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "RepositoryDefaultBranch",
                schema: "nao",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "RepositoryDevelopmentBranch",
                schema: "nao",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "RepositoryKey",
                schema: "nao",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "RepositoryUrl",
                schema: "nao",
                table: "AgentRuns");
        }
    }
}
