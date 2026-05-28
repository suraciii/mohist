using Microsoft.EntityFrameworkCore;

namespace Mohist.Server.Storage.Db;

public static class MohistDatabaseInitializer
{
    public static void Initialize(MohistDbContext db)
    {
        db.Database.EnsureCreated();
        EnsureAdditiveSchema(db);
    }

    private static void EnsureAdditiveSchema(MohistDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "WorkflowSessions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_WorkflowSessions" PRIMARY KEY,
                "WorkflowRunId" TEXT NOT NULL,
                "SessionName" TEXT NOT NULL,
                "AcpSessionId" TEXT NULL,
                "ProjectId" TEXT NULL,
                "IssueNumber" INTEGER NULL,
                "RunnerId" TEXT NULL,
                "Status" TEXT NOT NULL,
                "Model" TEXT NULL,
                "WorkDir" TEXT NULL,
                "ProcessPid" INTEGER NULL,
                "CreatedAt" TEXT NOT NULL,
                "StartedAt" TEXT NULL,
                "LastDataAt" TEXT NULL,
                "CompletedAt" TEXT NULL,
                "FailureReason" TEXT NULL,
                "ExitCode" INTEGER NULL
            );
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorkflowSessions_WorkflowRunId_SessionName"
            ON "WorkflowSessions" ("WorkflowRunId", "SessionName");
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS "IX_WorkflowSessions_AcpSessionId"
            ON "WorkflowSessions" ("AcpSessionId");
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS "IX_WorkflowSessions_ProjectId_IssueNumber_CreatedAt"
            ON "WorkflowSessions" ("ProjectId", "IssueNumber", "CreatedAt");
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "WorkflowSessionEvents" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_WorkflowSessionEvents" PRIMARY KEY AUTOINCREMENT,
                "WorkflowSessionId" TEXT NOT NULL,
                "WorkflowRunId" TEXT NOT NULL,
                "SessionName" TEXT NOT NULL,
                "AcpSessionId" TEXT NULL,
                "ProjectId" TEXT NULL,
                "IssueNumber" INTEGER NULL,
                "WorkId" TEXT NULL,
                "WorkType" TEXT NULL,
                "Stage" TEXT NULL,
                "Sequence" INTEGER NOT NULL,
                "Type" TEXT NOT NULL,
                "PayloadJson" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_WorkflowSessionEvents_WorkflowSessionId_Sequence"
            ON "WorkflowSessionEvents" ("WorkflowSessionId", "Sequence");
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS "IX_WorkflowSessionEvents_WorkflowRunId_SessionName_Sequence"
            ON "WorkflowSessionEvents" ("WorkflowRunId", "SessionName", "Sequence");
            """);

        db.Database.ExecuteSqlRaw("""
            CREATE INDEX IF NOT EXISTS "IX_WorkflowSessionEvents_ProjectId_IssueNumber_Id"
            ON "WorkflowSessionEvents" ("ProjectId", "IssueNumber", "Id");
            """);
    }
}
