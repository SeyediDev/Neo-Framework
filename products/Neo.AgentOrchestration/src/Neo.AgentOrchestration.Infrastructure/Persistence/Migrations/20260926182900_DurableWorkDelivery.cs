using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Neo.AgentOrchestration.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableWorkDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MessageName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    MessageType = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    MessageContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MessageResponse = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PublishError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PublishTryCount = table.Column<int>(type: "int", nullable: true),
                    ProcessError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ProcessTryCount = table.Column<int>(type: "int", nullable: true),
                    OutboxState = table.Column<int>(type: "int", nullable: false),
                    TenantKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    JobId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    DeliveryLeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeliveryLeaseUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpireDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    LastModified = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Deliveries",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkItemVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false, collation: "Latin1_General_100_BIN2"),
                    MessageId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    PayloadHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OutboxId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deliveries", x => x.Id);
                    table.UniqueConstraint("AK_Deliveries_Id_WorkItemId_ProjectId", x => new { x.Id, x.WorkItemId, x.ProjectId });
                    table.ForeignKey(
                        name: "FK_Deliveries_OutboxMessages_OutboxId",
                        column: x => x.OutboxId,
                        principalSchema: "nao",
                        principalTable: "OutboxMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Deliveries_Projects_ProjectId_WorkspaceId_OrganizationId",
                        columns: x => new { x.ProjectId, x.WorkspaceId, x.OrganizationId },
                        principalSchema: "nao",
                        principalTable: "Projects",
                        principalColumns: new[] { "Id", "WorkspaceId", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Deliveries_WorkItems_WorkItemId_ProjectId",
                        columns: x => new { x.WorkItemId, x.ProjectId },
                        principalSchema: "nao",
                        principalTable: "WorkItems",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InboxReceipts",
                schema: "nao",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkItemVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false, collation: "Latin1_General_100_BIN2"),
                    MessageId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    PayloadHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    AppliedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FollowUpId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InboxReceipts_Deliveries_FollowUpId_WorkItemId_ProjectId",
                        columns: x => new { x.FollowUpId, x.WorkItemId, x.ProjectId },
                        principalSchema: "nao",
                        principalTable: "Deliveries",
                        principalColumns: new[] { "Id", "WorkItemId", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InboxReceipts_Projects_ProjectId_WorkspaceId_OrganizationId",
                        columns: x => new { x.ProjectId, x.WorkspaceId, x.OrganizationId },
                        principalSchema: "nao",
                        principalTable: "Projects",
                        principalColumns: new[] { "Id", "WorkspaceId", "OrganizationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InboxReceipts_WorkItems_WorkItemId_ProjectId",
                        columns: x => new { x.WorkItemId, x.ProjectId },
                        principalSchema: "nao",
                        principalTable: "WorkItems",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_OutboxId",
                schema: "nao",
                table: "Deliveries",
                column: "OutboxId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_ProjectId_WorkspaceId_OrganizationId",
                schema: "nao",
                table: "Deliveries",
                columns: new[] { "ProjectId", "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_WorkItemId_ProjectId",
                schema: "nao",
                table: "Deliveries",
                columns: new[] { "WorkItemId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_WorkspaceId_Source_MessageId",
                schema: "nao",
                table: "Deliveries",
                columns: new[] { "WorkspaceId", "Source", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboxReceipts_FollowUpId_WorkItemId_ProjectId",
                schema: "nao",
                table: "InboxReceipts",
                columns: new[] { "FollowUpId", "WorkItemId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_InboxReceipts_ProjectId_WorkspaceId_OrganizationId",
                schema: "nao",
                table: "InboxReceipts",
                columns: new[] { "ProjectId", "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_InboxReceipts_WorkItemId_ProjectId",
                schema: "nao",
                table: "InboxReceipts",
                columns: new[] { "WorkItemId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_InboxReceipts_WorkspaceId_Source_MessageId",
                schema: "nao",
                table: "InboxReceipts",
                columns: new[] { "WorkspaceId", "Source", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_OutboxState_NextAttemptAtUtc_DeliveryLeaseUntilUtc",
                schema: "nao",
                table: "OutboxMessages",
                columns: new[] { "OutboxState", "NextAttemptAtUtc", "DeliveryLeaseUntilUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_TenantKey_IdempotencyKey",
                schema: "nao",
                table: "OutboxMessages",
                columns: new[] { "TenantKey", "IdempotencyKey" },
                unique: true,
                filter: "[TenantKey] IS NOT NULL AND [IdempotencyKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxReceipts",
                schema: "nao");

            migrationBuilder.DropTable(
                name: "Deliveries",
                schema: "nao");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "nao");
        }
    }
}
