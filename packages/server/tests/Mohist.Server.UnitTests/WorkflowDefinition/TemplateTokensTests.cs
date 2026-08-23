using System.Text.Json;
using Xunit;

namespace Mohist.Workflow.Definition.Tests;

public class TemplateTokensTests
{
    [Fact]
    public void Contains_NullJsonElement_ReturnsFalse()
    {
        Assert.False(TemplateTokens.Contains((JsonElement?)null));
    }

    [Fact]
    public void Contains_UndefinedJsonElement_ReturnsFalse()
    {
        var element = default(JsonElement);

        Assert.False(TemplateTokens.Contains((JsonElement?)element));
    }

    [Fact]
    public void Contains_NonStringScalar_ReturnsFalse()
    {
        var number = JsonDocument.Parse("42").RootElement.Clone();
        var boolean = JsonDocument.Parse("true").RootElement.Clone();
        var array = JsonDocument.Parse("[1, 2]").RootElement.Clone();
        var objectValue = JsonDocument.Parse("""{"a": 1}""").RootElement.Clone();

        Assert.False(TemplateTokens.Contains(number));
        Assert.False(TemplateTokens.Contains(boolean));
        Assert.False(TemplateTokens.Contains(array));
        Assert.False(TemplateTokens.Contains(objectValue));
    }

    [Fact]
    public void Contains_PlainStringWithoutToken_ReturnsFalse()
    {
        var element = JsonDocument.Parse("\"plain text\"").RootElement.Clone();

        Assert.False(TemplateTokens.Contains(element));
    }

    [Fact]
    public void Contains_StringWithSimpleToken_ReturnsTrue()
    {
        var element = JsonDocument.Parse("\"${{ vars.name }}\"").RootElement.Clone();

        Assert.True(TemplateTokens.Contains(element));
    }

    [Fact]
    public void Contains_StringWithMultiSegmentPath_ReturnsTrue()
    {
        var element = JsonDocument.Parse("\"${{ repository.baseBranch }}\"").RootElement.Clone();

        Assert.True(TemplateTokens.Contains(element));
    }

    [Fact]
    public void Contains_StringWithTokenInsideLargerText_ReturnsTrue()
    {
        var element = JsonDocument.Parse("\"prefix ${{ vars.x }} suffix\"").RootElement.Clone();

        Assert.True(TemplateTokens.Contains(element));
    }

    [Fact]
    public void Contains_ObjectWithTokenInAnyProperty_ReturnsTrue()
    {
        var element = JsonDocument.Parse("""{ "a": "no token", "b": "${{ vars.x }}" }""").RootElement.Clone();

        Assert.True(TemplateTokens.Contains(element));
    }

    [Fact]
    public void Contains_ObjectWithOnlyNonTokenStrings_ReturnsFalse()
    {
        var element = JsonDocument.Parse("""{ "a": "no token", "b": "still no token" }""").RootElement.Clone();

        Assert.False(TemplateTokens.Contains(element));
    }

    [Fact]
    public void Contains_ArrayWithTokenInAnyItem_ReturnsTrue()
    {
        var element = JsonDocument.Parse("""[ "no token", "${{ vars.x }}" ]""").RootElement.Clone();

        Assert.True(TemplateTokens.Contains(element));
    }

    [Fact]
    public void Contains_ArrayWithOnlyNonTokenStrings_ReturnsFalse()
    {
        var element = JsonDocument.Parse("""[ "no token", "still no token" ]""").RootElement.Clone();

        Assert.False(TemplateTokens.Contains(element));
    }

    [Fact]
    public void Contains_NestedObjectWithTokenDeepInside_ReturnsTrue()
    {
        var element = JsonDocument.Parse("""
            {
              "outer": {
                "inner": {
                  "deep": "${{ vars.x }}"
                }
              }
            }
            """).RootElement.Clone();

        Assert.True(TemplateTokens.Contains(element));
    }

    [Fact]
    public void Contains_StringWithBrokenTokenSyntax_ReturnsFalse()
    {
        var unclosed = JsonDocument.Parse("\"${{ vars.x\"").RootElement.Clone();
        var noBraces = JsonDocument.Parse("\"vars.x\"").RootElement.Clone();
        var singleBrace = JsonDocument.Parse("\"${vars.x}\"").RootElement.Clone();

        Assert.False(TemplateTokens.Contains(unclosed));
        Assert.False(TemplateTokens.Contains(noBraces));
        Assert.False(TemplateTokens.Contains(singleBrace));
    }
}
