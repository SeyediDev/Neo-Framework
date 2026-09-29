using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Neo.AgentOrchestration.Infrastructure.Persistence.Migrations;

// Historical unregistered draft: it intentionally has no Migration/DbContext
// attributes and was never applied by EF. TypedWorkItems adopts the imported
// table with data-preserving guards and a generated model snapshot instead.
public partial class FormalizeOwnerHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "WorkItemOwnerHistory",
            schema: "nao",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                WorkItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SourceWorkItemId = table.Column<long>(type: "bigint", nullable: false),
                OriginalStatus = table.Column<int>(type: "int", nullable: false),
                OwnerRole = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                OwnerAgent = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                OwnerChat = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkItemOwnerHistory", x => x.Id);
                table.ForeignKey("FK_WorkItemOwnerHistory_WorkItems_WorkItemId_ProjectId",
                    x => new { x.WorkItemId, x.ProjectId }, "nao.WorkItems", new[] { "Id", "ProjectId" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_WorkItemOwnerHistory_ProjectId_WorkItemId_CreatedAtUtc",
            schema: "nao", table: "WorkItemOwnerHistory",
            columns: new[] { "ProjectId", "WorkItemId", "CreatedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "WorkItemOwnerHistory", schema: "nao");
}
