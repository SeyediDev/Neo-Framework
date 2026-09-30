using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Neo.AgentOrchestration.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProjectTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectTemplates",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", maxLength: 64000, nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedByAgent = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedByChat = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectTemplates", x => x.Id);
                    table.UniqueConstraint("AK_ProjectTemplates_Id_WorkspaceId_OrganizationId", x => new { x.Id, x.WorkspaceId, x.OrganizationId });
                    table.CheckConstraint("CK_Template_Revision", "[Revision] > 0");
                    table.ForeignKey(
                        name: "FK_ProjectTemplates_Workspaces_WorkspaceId_OrganizationId",
                        columns: x => new { x.WorkspaceId, x.OrganizationId },
                        principalSchema: "nao",
                        principalTable: "Workspaces",
                        principalColumns: new[] { "Id", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TemplateInstantiations",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", maxLength: 40000, nullable: false),
                    MappingJson = table.Column<string>(type: "nvarchar(max)", maxLength: 40000, nullable: false),
                    CreatedByAgent = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedByChat = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemplateInstantiations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TemplateInstantiations_ProjectTemplates_TemplateId_WorkspaceId_OrganizationId",
                        columns: x => new { x.TemplateId, x.WorkspaceId, x.OrganizationId },
                        principalSchema: "nao",
                        principalTable: "ProjectTemplates",
                        principalColumns: new[] { "Id", "WorkspaceId", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TemplateInstantiations_Projects_ProjectId_WorkspaceId_OrganizationId",
                        columns: x => new { x.ProjectId, x.WorkspaceId, x.OrganizationId },
                        principalSchema: "nao",
                        principalTable: "Projects",
                        principalColumns: new[] { "Id", "WorkspaceId", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTemplates_WorkspaceId_Key_Revision",
                schema: "nao",
                table: "ProjectTemplates",
                columns: new[] { "WorkspaceId", "Key", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTemplates_WorkspaceId_OrganizationId",
                schema: "nao",
                table: "ProjectTemplates",
                columns: new[] { "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_TemplateInstantiations_ProjectId",
                schema: "nao",
                table: "TemplateInstantiations",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TemplateInstantiations_ProjectId_WorkspaceId_OrganizationId",
                schema: "nao",
                table: "TemplateInstantiations",
                columns: new[] { "ProjectId", "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_TemplateInstantiations_TemplateId_WorkspaceId_OrganizationId",
                schema: "nao",
                table: "TemplateInstantiations",
                columns: new[] { "TemplateId", "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_TemplateInstantiations_WorkspaceId_RequestId",
                schema: "nao",
                table: "TemplateInstantiations",
                columns: new[] { "WorkspaceId", "RequestId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Forward-only: preserve immutable template versions and project provenance.");
        }
    }
}
