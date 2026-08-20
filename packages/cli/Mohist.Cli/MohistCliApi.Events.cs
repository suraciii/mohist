namespace Mohist.Cli;

internal sealed partial class MohistCliApi
{
    internal TextWriter Output => _out;
    internal TextWriter Error => _err;
    internal HttpClient Http => _http;
    internal Func<TimeSpan, CancellationToken, Task> PollWait => _pollWait;
    internal EventSocketStream? EventStream { get; }

    private EventSocketStream? CreateEventStream(
        CliCredentialSession? credentialSession,
        IEventSocketFactory? eventSocketFactory,
        Func<double>? eventReconnectJitter)
    {
        if (credentialSession is null)
            return null;

        return new EventSocketStream(
            _http,
            credentialSession,
            eventSocketFactory ?? new ClientEventSocketFactory(),
            _out,
            _err,
            _pollWait,
            eventReconnectJitter ?? Random.Shared.NextDouble,
            _timeProvider);
    }
}
