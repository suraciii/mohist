namespace Mohist.Cli;

internal sealed class UpdateOutcomeReporter
{
    private readonly HttpClient _http;
    private readonly TextWriter _out;

    public UpdateOutcomeReporter(HttpClient http, TextWriter output)
    {
        _http = http;
        _out = output;
    }

    public async Task<bool> PostAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
        {
            _out.WriteLine($"Could not persist update outcome to server (HTTP {(int)response.StatusCode}). The CLI terminal output above is the authoritative result.");
            return false;
        }

        _out.WriteLine("Update outcome persisted to server.");
        return true;
    }
}
