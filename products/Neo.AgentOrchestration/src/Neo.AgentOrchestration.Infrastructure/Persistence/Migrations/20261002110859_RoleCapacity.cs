using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Neo.AgentOrchestration.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RoleCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkItems_OwnerRoleId",
                schema: "nao",
                table: "WorkItems");

            migrationBuilder.AddColumn<int>(
                name: "MaxConcurrentWorkItems",
                schema: "nao",
                table: "RoleProfiles",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_OwnerRoleId",
                schema: "nao",
                table: "WorkItems",
                column: "OwnerRoleId",
                filter: "[Status] = 3 AND [OwnerRoleId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkItems_OwnerRoleId",
                schema: "nao",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "MaxConcurrentWorkItems",
                schema: "nao",
                table: "RoleProfiles");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_OwnerRoleId",
                schema: "nao",
                table: "WorkItems",
                column: "OwnerRoleId",
                unique: true,
                filter: "[Status] = 3 AND [OwnerRoleId] IS NOT NULL");
        }
    }
}
