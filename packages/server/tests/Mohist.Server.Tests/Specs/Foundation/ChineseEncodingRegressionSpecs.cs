using System.Net.Http.Json;
using Mohist.Server.Project.Services;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.Foundation;

/// <summary>
/// 锁定 API 响应中文字符不被 \uXXXX 转义的回归保护。
/// </summary>
[Collection("MohistIntegration")]
public class ChineseEncodingRegressionSpecs(MohistIntegrationFixture fixture)
{
    private readonly HttpClient _client = fixture.Client;

    [Trait(Traits.Speed.Name, Traits.Speed.Integration)]
    [Trait(Traits.Sut.Name, Traits.Sut.Project)]
    [Fact]
    public async Task ApiResponse_KeepsChineseCharacters_Unescaped()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var project = await _client.PostDataAsync<ProjectInfo>("/api/projects", new { name = $"zh-enc-{suffix}" });

        using var response = await _client.PostAsJsonAsync(
            $"/api/projects/{project.Id}/epics",
            new { title = "中文史诗标题", description = "中文描述" });
        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadAsStringAsync();

        Assert.Contains("中文史诗标题", raw);
        Assert.Contains("中文描述", raw);
    }
}
