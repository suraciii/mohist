using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.GitHub;

namespace Mohist.Server.Infrastructure.Data.Db;

public partial class MohistDbContext
{
    public DbSet<GitHubIssueCommentOperationRow> GitHubIssueCommentOperations { get; set; } = null!;
}
