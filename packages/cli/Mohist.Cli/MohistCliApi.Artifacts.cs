namespace Mohist.Cli;

internal sealed partial class MohistCliApi
{
    internal Func<Stream, CancellationToken, Task>? ArtifactBinaryOutput { get; set; }

    public async Task<int> StreamGetAsync(string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path, body: null);
        if (response is null) return 1;
        if (!response.IsSuccessStatusCode) return await PrintResponseAsync(response).ConfigureAwait(false);
        await using var content = await response.Content.ReadAsStreamAsync(Invocation.CancellationToken).ConfigureAwait(false);
        if (ArtifactBinaryOutput is not null)
        {
            await ArtifactBinaryOutput(content, Invocation.CancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (ReferenceEquals(_out, Console.Out))
        {
            await content.CopyToAsync(Console.OpenStandardOutput(), Invocation.CancellationToken).ConfigureAwait(false);
            return 0;
        }

        using var reader = new StreamReader(content, leaveOpen: true);
        await _out.WriteAsync(await reader.ReadToEndAsync(Invocation.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        return 0;
    }
}
