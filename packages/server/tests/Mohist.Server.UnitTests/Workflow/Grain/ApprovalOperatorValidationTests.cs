using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Grain;

/// <summary>
/// issue-491 T-002: validation parity with the comment author model. Approval
/// <c>--author</c> is a declared name, not an authenticated identity: required,
/// trimmed of surrounding whitespace, capped at 100 characters. Mirrors
/// <see cref="Mohist.Server.Issue.Grains.IssueGrain.AddCommentAsync(string, string, string[]?)"/>
/// so the comment and the approval author share one validation contract.
/// </summary>
public class ApprovalOperatorValidationTests
{
    [Fact]
    public void Normalize_TrimsSurroundingWhitespace()
    {
        Assert.Equal("supervisor", ApprovalOperatorValidation.Normalize("  supervisor  "));
    }

    [Fact]
    public void Normalize_PassesThroughExactLength()
    {
        var exactly = new string('a', ApprovalOperatorValidation.MaxLength);
        Assert.Equal(exactly, ApprovalOperatorValidation.Normalize(exactly));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void Normalize_RejectsBlankOrWhitespace(string? raw)
    {
        var ex = Assert.Throws<ArgumentException>(() => ApprovalOperatorValidation.Normalize(raw));
        Assert.Contains("required", ex.Message);
    }

    [Fact]
    public void Normalize_RejectsOverlongAuthor()
    {
        var overlong = new string('a', ApprovalOperatorValidation.MaxLength + 1);
        var ex = Assert.Throws<ArgumentException>(() => ApprovalOperatorValidation.Normalize(overlong));
        Assert.Contains("100 characters", ex.Message);
    }

}
