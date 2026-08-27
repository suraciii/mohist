using System.Security.Cryptography;
using System.Text.Json;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Runner.Grains;

public static class WorkResultFingerprint
{
    public static string For(WorkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // ErrorCode is computed by the Server and is not part of the Runner payload.
        var canonical = new
        {
            status = result.Status,
            message = result.Message,
            output = result.Output,
            exitCode = result.ExitCode,
            artifactUploadIds = result.ArtifactUploadIds,
            addTasks = result.AddTasks,
            error = result.Error,
        };
        return Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(canonical, JSON.Options))).ToLowerInvariant();
    }
}
