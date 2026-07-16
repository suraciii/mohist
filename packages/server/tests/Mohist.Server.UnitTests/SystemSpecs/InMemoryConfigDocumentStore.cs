using Mohist.Server.Infrastructure.Config;

namespace Mohist.Server.UnitTests.SystemSpecs;

internal sealed class InMemoryConfigDocumentStore : IConfigDocumentStore
{
    public string Location => "/mohist-tests/config.jsonc";

    public string? Content { get; set; }

    public string? Read() => Content;

    public Task WriteAsync(string content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Content = content;
        return Task.CompletedTask;
    }
}
