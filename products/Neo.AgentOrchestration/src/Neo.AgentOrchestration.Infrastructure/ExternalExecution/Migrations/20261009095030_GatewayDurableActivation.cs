using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Neo.AgentOrchestration.Infrastructure.ExternalExecution.Migrations
{
    /// <inheritdoc />
    public partial class GatewayDurableActivation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "gateway",
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
                name: "Activations",
                schema: "gateway",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    OutboxId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Activations_OutboxMessages_OutboxId",
                        column: x => x.OutboxId,
                        principalSchema: "gateway",
                        principalTable: "OutboxMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Activations_Runs_RunId_OrganizationId_WorkspaceId_ProjectId",
                        columns: x => new { x.RunId, x.OrganizationId, x.WorkspaceId, x.ProjectId },
                        principalSchema: "gateway",
                        principalTable: "Runs",
                        principalColumns: new[] { "RunId", "OrganizationId", "WorkspaceId", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activations_OutboxId",
                schema: "gateway",
                table: "Activations",
                column: "OutboxId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Activations_RunId_OrganizationId_WorkspaceId_ProjectId",
                schema: "gateway",
                table: "Activations",
                columns: new[] { "RunId", "OrganizationId", "WorkspaceId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_Activations_RunId_Sequence",
                schema: "gateway",
                table: "Activations",
                columns: new[] { "RunId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_OutboxState_NextAttemptAtUtc_DeliveryLeaseUntilUtc",
                schema: "gateway",
                table: "OutboxMessages",
                columns: new[] { "OutboxState", "NextAttemptAtUtc", "DeliveryLeaseUntilUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_TenantKey_IdempotencyKey",
                schema: "gateway",
                table: "OutboxMessages",
                columns: new[] { "TenantKey", "IdempotencyKey" },
                unique: true,
                filter: "[TenantKey] IS NOT NULL AND [IdempotencyKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Activations",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "gateway");
        }
    }
}
