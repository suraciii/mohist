using System.Net;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal sealed partial class MohistCliApi
{
    internal async Task<JsonNode?> PostManagerManagementAsync(JsonObject request)
    {
        var response = await ResponseReader.ReadAsync(
            HttpMethod.Post,
            "/api/slack-manager/management",
            request,
            mutating: true,
            cancellationToken: Invocation.CancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
            throw new ApiResponseException(response.StatusCode, response.Failure!.Message, response.Failure.Code, response.Failure.Details);
        return response.Data;
    }
}
