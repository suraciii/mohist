using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Issue.Data;

public class IssueDerivedColumnsSpecs
{
    [Fact]
    public async Task DerivedColumn_TracksStateAfterUpdate()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        await using var db = database.CreateContext();

        var issue = new IssueRow
        {
            ProjectId = "proj_1",
            Number = 1,
            State = """{"projectId":"proj_1","number":1,"title":"Old title","priority":"p2","isDraft":false,"prerequisiteNumbers":[2,3]}"""
        };
        db.Issues.Add(issue);
        await db.SaveChangesAsync();

        issue.State = """{"projectId":"proj_1","number":1,"title":"New title","priority":"p1","isDraft":true,"prerequisiteNumbers":[4]}""";
        await db.SaveChangesAsync();

        var read = await db.Issues.AsNoTracking().SingleAsync(i => i.ProjectId == "proj_1" && i.Number == 1);

        Assert.Equal("New title", read.Title);
        Assert.Equal("p1", read.Priority);
        Assert.True(read.IsDraft);
        Assert.Equal("[4]", read.PrerequisiteNumbersJson);
    }

    [Fact]
    public async Task DerivedColumn_MissingOrLegacyKeys_YieldNullSafely()
    {
        await using var database = TestSqliteDatabase.CreateMigrated();
        await using var db = database.CreateContext();

        var legacyIssue = new IssueRow
        {
            ProjectId = "proj_1",
            Number = 2,
            State = """{"ProjectId":"proj_1","Number":2,"Status":"backlog","Title":"Legacy title","Priority":"P0"}"""
        };
        var sparseIssue = new IssueRow
        {
            ProjectId = "proj_1",
            Number = 3,
            State = """{"projectId":"proj_1","number":3,"status":"backlog"}"""
        };

        db.Issues.Add(legacyIssue);
        db.Issues.Add(sparseIssue);
        await db.SaveChangesAsync();

        var legacyRead = await db.Issues.AsNoTracking().SingleAsync(i => i.ProjectId == "proj_1" && i.Number == 2);
        var sparseRead = await db.Issues.AsNoTracking().SingleAsync(i => i.ProjectId == "proj_1" && i.Number == 3);

        Assert.Equal("Legacy title", legacyRead.Title);
        Assert.Equal("P0", legacyRead.Priority);
        Assert.Null(legacyRead.IsDraft);
        Assert.Null(legacyRead.PrerequisiteNumbersJson);

        Assert.Null(sparseRead.Title);
        Assert.Null(sparseRead.Priority);
        Assert.Null(sparseRead.IsDraft);
        Assert.Null(sparseRead.PrerequisiteNumbersJson);
    }
}
