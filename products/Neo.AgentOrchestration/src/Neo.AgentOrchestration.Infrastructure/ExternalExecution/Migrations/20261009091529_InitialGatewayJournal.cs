using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Neo.AgentOrchestration.Infrastructure.ExternalExecution.Migrations
{
    /// <inheritdoc />
    public partial class InitialGatewayJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "gateway");

            migrationBuilder.CreateTable(
                name: "Runs",
                schema: "gateway",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BindingKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    BindingFingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    PayloadHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SandboxId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Phase = table.Column<int>(type: "int", nullable: false),
                    PreparedHandle = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: true),
                    ApprovalId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ApprovalPending = table.Column<bool>(type: "bit", nullable: false),
                    StopPending = table.Column<bool>(type: "bit", nullable: false),
                    CapacityHeld = table.Column<bool>(type: "bit", nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FrozenResult = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CallbackReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.RunId);
                    table.UniqueConstraint("AK_Runs_RunId_OrganizationId_WorkspaceId_ProjectId", x => new { x.RunId, x.OrganizationId, x.WorkspaceId, x.ProjectId });
                });

            migrationBuilder.CreateTable(
                name: "Usage",
                schema: "gateway",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReportId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BodyHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Usage", x => new { x.RunId, x.ReportId });
                    table.ForeignKey(
                        name: "FK_Usage_Runs_RunId_OrganizationId_WorkspaceId_ProjectId",
                        columns: x => new { x.RunId, x.OrganizationId, x.WorkspaceId, x.ProjectId },
                        principalSchema: "gateway",
                        principalTable: "Runs",
                        principalColumns: new[] { "RunId", "OrganizationId", "WorkspaceId", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_CapacityHeld_LeaseUntilUtc_Phase",
                schema: "gateway",
                table: "Runs",
                columns: new[] { "CapacityHeld", "LeaseUntilUtc", "Phase" });

            migrationBuilder.CreateIndex(
                name: "IX_Usage_RunId_OrganizationId_WorkspaceId_ProjectId",
                schema: "gateway",
                table: "Usage",
                columns: new[] { "RunId", "OrganizationId", "WorkspaceId", "ProjectId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Usage",
                schema: "gateway");

            migrationBuilder.DropTable(
                name: "Runs",
                schema: "gateway");
        }
    }
}
