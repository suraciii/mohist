using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal static class NdjsonStream
{
    public static async Task<int> ReadAsync(
        HttpClient http,
        string path,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        HttpResponseMessage response;
        try
        {
            response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (HttpRequestException)
        {
            await error.WriteLineAsync("Server is not running. Start with: mo server start").ConfigureAwait(false);
            return 1;
        }

        try
        {
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var diagnostic = await TryReadBadRequestDiagnosticAsync(response, cancellationToken).ConfigureAwait(false);
                if (diagnostic is not null)
                    await error.WriteLineAsync(diagnostic).ConfigureAwait(false);
                else
                    await error.WriteLineAsync(await ReadErrorMessageAsync(response, cancellationToken).ConfigureAwait(false))
                        .ConfigureAwait(false);
                return 1;
            }

            if (!response.IsSuccessStatusCode)
            {
                await error.WriteLineAsync(
                        $"Tail request failed: {(int)response.StatusCode} {response.ReasonPhrase}")
                    .ConfigureAwait(false);
                return 1;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;
                await output.WriteLineAsync(line).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            response.Dispose();
        }
    }

    private static async Task<string?> TryReadBadRequestDiagnosticAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        JsonNode? node = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            if (stream.CanSeek && stream.Length == 0)
                return null;
            node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        if (node is null)
            return null;

        var details = node["details"] as JsonObject;
        var offset = details?["offset"]?.GetValue<int?>();
        var line = details?["line"]?.GetValue<int?>();
        var column = details?["column"]?.GetValue<int?>();
        var message = node["error"]?.GetValue<string>();
        if (string.IsNullOrEmpty(message))
            return null;

        var location = BuildLocation(offset, line, column);
        return string.IsNullOrEmpty(location)
            ? message!
            : $"{message} ({location})";
    }

    private static async Task<string> ReadErrorMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var msg = node?["error"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(msg))
                return msg!;
        }
        catch
        {
        }
        return $"Tail request failed: {(int)response.StatusCode} {response.ReasonPhrase}";
    }

    private static string BuildLocation(int? offset, int? line, int? column)
    {
        if (line is not null && column is not null)
            return $"line {line}, column {column}";
        if (offset is not null)
            return $"offset {offset}";
        return string.Empty;
    }

    public static async Task<int> ReadSelectedAsync(
        HttpClient http,
        string path,
        TextWriter output,
        TextWriter error,
        JsonSelection selection,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (HttpRequestException ex)
        {
            await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }

        try
        {
            if (!response.IsSuccessStatusCode)
            {
                await error.WriteLineAsync($"Tail request failed: {(int)response.StatusCode} {response.ReasonPhrase}")
                    .ConfigureAwait(false);
                return 1;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;
                var node = JsonNode.Parse(line);
                var projected = selection.Project(node, ResourceCardinality.Stream);
                await output.WriteLineAsync(projected.ToJsonString(MohistCliApi.JsonCompactOutputOptions)).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (JsonException ex)
        {
            await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            response.Dispose();
        }
    }
}
