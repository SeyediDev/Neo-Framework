using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Neo.AgentOrchestration.Infrastructure.Persistence.Migrations;

public partial class TypedWorkItems : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "AcceptanceCriteria", schema: "nao", table: "WorkItems",
            type: "nvarchar(max)", maxLength: 8000, nullable: true);
        migrationBuilder.AddColumn<byte>(name: "Type", schema: "nao", table: "WorkItems",
            type: "tinyint", nullable: false, defaultValue: (byte)1);
        migrationBuilder.AddCheckConstraint(name: "CK_WorkItems_Type", schema: "nao", table: "WorkItems",
            sql: "[Type] BETWEEN 1 AND 4");

        // The legacy importer provisioned this table before it had an EF migration.
        // Adopt it in place, or create it for a fresh installation. Never drop or
        // copy imported records. Invalid legacy shapes/orphans fail the transaction.
        migrationBuilder.Sql("""
            IF OBJECT_ID(N'[nao].[WorkItemOwnerHistory]', N'U') IS NULL
            BEGIN
                CREATE TABLE [nao].[WorkItemOwnerHistory] (
                    [Id] uniqueidentifier NOT NULL,
                    [ProjectId] uniqueidentifier NOT NULL,
                    [WorkItemId] uniqueidentifier NOT NULL,
                    [SourceWorkItemId] bigint NOT NULL,
                    [OriginalStatus] int NOT NULL,
                    [OwnerRole] nvarchar(200) NULL,
                    [OwnerAgent] nvarchar(200) NULL,
                    [OwnerChat] nvarchar(200) NULL,
                    [CreatedAtUtc] datetimeoffset NOT NULL,
                    [RowVersion] rowversion NULL,
                    CONSTRAINT [PK_WorkItemOwnerHistory] PRIMARY KEY ([Id])
                );
            END;
            IF COL_LENGTH(N'nao.WorkItemOwnerHistory', N'RowVersion') IS NULL
                ALTER TABLE [nao].[WorkItemOwnerHistory] ADD [RowVersion] rowversion NULL;
            IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'nao.WorkItemOwnerHistory')
                       AND name=N'CreatedAtUtc' AND TYPE_NAME(user_type_id)=N'datetime2')
                ALTER TABLE [nao].[WorkItemOwnerHistory] ALTER COLUMN [CreatedAtUtc] datetimeoffset NOT NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'nao.WorkItemOwnerHistory')
                           AND name=N'CreatedAtUtc' AND TYPE_NAME(user_type_id)=N'datetimeoffset')
                THROW 51000, 'Unsupported owner-history timestamp schema; inspect without deleting data.', 1;
            IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'nao.WorkItemOwnerHistory')
                           AND name=N'FK_WorkItemOwnerHistory_WorkItems_WorkItemId_ProjectId')
                ALTER TABLE [nao].[WorkItemOwnerHistory] WITH CHECK ADD
                    CONSTRAINT [FK_WorkItemOwnerHistory_WorkItems_WorkItemId_ProjectId]
                    FOREIGN KEY ([WorkItemId], [ProjectId]) REFERENCES [nao].[WorkItems] ([Id], [ProjectId]);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'nao.WorkItemOwnerHistory')
                           AND name=N'IX_WorkItemOwnerHistory_ProjectId_WorkItemId_CreatedAtUtc')
                CREATE INDEX [IX_WorkItemOwnerHistory_ProjectId_WorkItemId_CreatedAtUtc]
                    ON [nao].[WorkItemOwnerHistory] ([ProjectId], [WorkItemId], [CreatedAtUtc]);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'nao.WorkItemOwnerHistory')
                           AND name=N'IX_WorkItemOwnerHistory_WorkItemId_ProjectId')
                CREATE INDEX [IX_WorkItemOwnerHistory_WorkItemId_ProjectId]
                    ON [nao].[WorkItemOwnerHistory] ([WorkItemId], [ProjectId]);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException("Forward-only migration: preserve imported history and typed acceptance data. Restore an approved backup or apply a reviewed forward repair.");
}
