using System.Text;
using Microsoft.AspNetCore.Http;
using Mohist.Server.Api;
using Xunit;

namespace Mohist.Server.UnitTests.Api;

public sealed class IssueRoutesRequestTests
{
    [Fact]
    public async Task UpdateIssueRequest_BindsRiskAndRecordsFieldPresence()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"risk\":\"high\"}"));

        var request = await UpdateIssueRequest.BindAsync(context);

        Assert.NotNull(request);
        Assert.Equal("high", request!.Risk);
        Assert.True(request.Contains(nameof(UpdateIssueRequest.Risk)));
    }
}
