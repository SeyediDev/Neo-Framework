using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Neo.AgentOrchestration.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HttpHarnessDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowExternalExecution",
                schema: "nao",
                table: "AgentRuns",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "HarnessBinding",
                schema: "nao",
                table: "AgentRuns",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HarnessPayload",
                schema: "nao",
                table: "AgentRuns",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowExternalExecution",
                schema: "nao",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "HarnessBinding",
                schema: "nao",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "HarnessPayload",
                schema: "nao",
                table: "AgentRuns");
        }
    }
}
