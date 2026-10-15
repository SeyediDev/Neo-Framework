using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Neo.AgentOrchestration.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PrivateAdvisoryRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdvisoryRequests",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkItemVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequesterId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RequesterChatId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Fingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Brief = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    State = table.Column<byte>(type: "tinyint", nullable: false),
                    CancelRequested = table.Column<bool>(type: "bit", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Draft = table.Column<string>(type: "nvarchar(max)", maxLength: 12000, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NativeRunId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    InputTokens = table.Column<long>(type: "bigint", nullable: true),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeadlineUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdvisoryRequests", x => x.Id);
                    table.CheckConstraint("CK_AdvisoryRequests_State", "[State] BETWEEN 1 AND 7");
                    table.CheckConstraint("CK_AdvisoryRequests_Usage", "([InputTokens] IS NULL OR [InputTokens]>=0) AND ([OutputTokens] IS NULL OR [OutputTokens]>=0)");
                    table.ForeignKey(
                        name: "FK_AdvisoryRequests_Deliveries_DeliveryOperationId_WorkItemId_ProjectId",
                        columns: x => new { x.DeliveryOperationId, x.WorkItemId, x.ProjectId },
                        principalSchema: "nao",
                        principalTable: "Deliveries",
                        principalColumns: new[] { "Id", "WorkItemId", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AdvisoryRequests_Projects_ProjectId_WorkspaceId_OrganizationId",
                        columns: x => new { x.ProjectId, x.WorkspaceId, x.OrganizationId },
                        principalSchema: "nao",
                        principalTable: "Projects",
                        principalColumns: new[] { "Id", "WorkspaceId", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AdvisoryRequests_WorkItems_WorkItemId_ProjectId",
                        columns: x => new { x.WorkItemId, x.ProjectId },
                        principalSchema: "nao",
                        principalTable: "WorkItems",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryRequests_DeliveryOperationId",
                schema: "nao",
                table: "AdvisoryRequests",
                column: "DeliveryOperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryRequests_DeliveryOperationId_WorkItemId_ProjectId",
                schema: "nao",
                table: "AdvisoryRequests",
                columns: new[] { "DeliveryOperationId", "WorkItemId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryRequests_ProjectId_WorkspaceId_OrganizationId",
                schema: "nao",
                table: "AdvisoryRequests",
                columns: new[] { "ProjectId", "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryRequests_WorkItemId_ProjectId",
                schema: "nao",
                table: "AdvisoryRequests",
                columns: new[] { "WorkItemId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryRequests_WorkspaceId_RequesterId_RequestId",
                schema: "nao",
                table: "AdvisoryRequests",
                columns: new[] { "WorkspaceId", "RequesterId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryRequests_WorkspaceId_WorkItemId_RequesterId_CreatedAtUtc",
                schema: "nao",
                table: "AdvisoryRequests",
                columns: new[] { "WorkspaceId", "WorkItemId", "RequesterId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdvisoryRequests",
                schema: "nao");
        }
    }
}
