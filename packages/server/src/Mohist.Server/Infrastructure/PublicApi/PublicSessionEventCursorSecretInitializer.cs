using Microsoft.Extensions.Hosting;

namespace Mohist.Server.Infrastructure.PublicApi;

/// <summary>
/// Materializes the deployment-wide cursor key during startup. The codec
/// repeats the same ensure operation for hosts that use it without the
/// normal hosted startup path, while this service makes first-start key
/// persistence explicit for production.
/// </summary>
public sealed class PublicSessionEventCursorSecretInitializer : IHostedService
{
    private readonly PublicSessionEventCursorCodec _codec;

    public PublicSessionEventCursorSecretInitializer(PublicSessionEventCursorCodec codec)
    {
        _codec = codec;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _ = await _codec.OpenAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
