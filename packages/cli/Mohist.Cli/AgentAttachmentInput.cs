namespace Mohist.Cli;

internal sealed record UploadedAgentAttachment(
    string SourcePath,
    string FileName,
    string Id,
    string? ContentType,
    long Size);

internal static class AgentAttachmentInput
{
    public static async Task<IReadOnlyList<UploadedAgentAttachment>?> UploadAsync(
        MohistCliApi api,
        string projectId,
        IReadOnlyList<string> paths,
        string outputMode)
    {
        if (paths.Count == 0)
            return [];

        var pending = new List<PendingAgentAttachment>(paths.Count);
        foreach (var path in paths)
        {
            try
            {
                using var stream = api.FileSystem.OpenRead(path);
                pending.Add(new PendingAgentAttachment(
                    path,
                    Path.GetFileName(path),
                    stream.Length));
            }
            catch (Exception ex)
            {
                WriteUploadFailure(api, outputMode, path, "upload-failed", ex.Message);
                return null;
            }
        }

        WritePending(api, outputMode, pending);

        var uploaded = new List<UploadedAgentAttachment>(pending.Count);
        foreach (var item in pending)
        {
            try
            {
                var data = await api.UploadAttachmentAsync(projectId, item.SourcePath).ConfigureAwait(false);
                var id = data?["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidOperationException("Server returned an attachment without an id.");

                uploaded.Add(new UploadedAgentAttachment(
                    item.SourcePath,
                    data?["fileName"]?.GetValue<string>() ?? item.FileName,
                    id,
                    data?["contentType"]?.GetValue<string>(),
                    data?["size"]?.GetValue<long>() ?? item.Size));
            }
            catch (MohistCliApi.ApiResponseException ex)
            {
                WriteUploadFailure(api, outputMode, item.SourcePath, ex.Code ?? "upload-failed", ex.Message);
                return null;
            }
            catch (HttpRequestException ex)
            {
                WriteUploadFailure(api, outputMode, item.SourcePath, "server-unavailable", ex.Message);
                return null;
            }
            catch (Exception ex)
            {
                WriteUploadFailure(api, outputMode, item.SourcePath, "upload-failed", ex.Message);
                return null;
            }
        }

        WriteReady(api, outputMode, uploaded);
        return uploaded;
    }

    public static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".c" or ".h" => "text/x-c",
        ".cs" => "text/plain",
        ".css" => "text/css",
        ".csv" => "text/csv",
        ".html" or ".htm" => "text/html",
        ".java" => "text/x-java-source",
        ".js" or ".mjs" or ".cjs" => "text/javascript",
        ".json" => "application/json",
        ".md" or ".markdown" => "text/markdown",
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".ts" or ".tsx" => "text/typescript",
        ".txt" => "text/plain",
        ".xml" => "application/xml",
        ".yaml" or ".yml" => "text/yaml",
        _ => "application/octet-stream",
    };

    private static void WritePending(
        MohistCliApi api,
        string outputMode,
        IReadOnlyList<PendingAgentAttachment> pending)
    {
        WriteLines(api, outputMode, "Attachments to submit:", pending.Select(item =>
            $"  {item.SourcePath} ({item.FileName}, {FormatSize(item.Size)})"));
    }

    private static void WriteReady(
        MohistCliApi api,
        string outputMode,
        IReadOnlyList<UploadedAgentAttachment> uploaded)
    {
        WriteLines(api, outputMode, "Attachments ready:", uploaded.Select(item =>
            $"  {item.FileName} ({FormatSize(item.Size)}, id={item.Id})"));
    }

    private static void WriteUploadFailure(
        MohistCliApi api,
        string outputMode,
        string path,
        string reason,
        string message)
    {
        WriteLines(api, outputMode, "Attachment rejected:", [$"  {path} ({reason}): {message}"]);
    }

    private static void WriteLines(
        MohistCliApi api,
        string outputMode,
        string heading,
        IEnumerable<string> lines)
    {
        var writer = outputMode == "table" ? api.Output : api.Error;
        writer.WriteLine(heading);
        foreach (var line in lines)
            writer.WriteLine(line);
    }

    private static string FormatSize(long size)
    {
        if (size < 1024) return $"{size} B";
        if (size < 1024 * 1024) return $"{size / 1024d:0.0} KB";
        return $"{size / 1024d / 1024d:0.0} MB";
    }

    private sealed record PendingAgentAttachment(string SourcePath, string FileName, long Size);
}
