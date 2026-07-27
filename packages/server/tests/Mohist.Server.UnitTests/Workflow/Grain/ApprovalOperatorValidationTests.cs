using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Grain;

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
    public void Normalize_TreatsBlankOrWhitespaceAsMissing(string? raw)
    {
        Assert.Null(ApprovalOperatorValidation.Normalize(raw));
    }

    [Fact]
    public void Normalize_RejectsOverlongAuthor()
    {
        var overlong = new string('a', ApprovalOperatorValidation.MaxLength + 1);
        var ex = Assert.Throws<ArgumentException>(() => ApprovalOperatorValidation.Normalize(overlong));
        Assert.Contains("100 characters", ex.Message);
    }
}
