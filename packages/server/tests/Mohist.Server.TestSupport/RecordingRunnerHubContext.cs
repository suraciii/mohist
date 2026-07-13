using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Runner.Services.SignalR;

namespace Mohist.Server.TestSupport;

public sealed class RecordingRunnerHubContext : IHubContext<RunnerHub>
{
    private readonly RecordingHubClients _clients;
    private readonly Dictionary<string, object?> _invocationResponses = new(StringComparer.Ordinal);

    public RecordingRunnerHubContext()
    {
        _clients = new RecordingHubClients(this);
    }

    public List<RecordedRunnerHubMessage> SentMessages { get; } = [];
    public List<RecordedRunnerHubInvocation> Invocations { get; } = [];
    public IHubClients Clients => _clients;
    public IGroupManager Groups { get; } = new NoopGroupManager();

    public void Clear()
    {
        SentMessages.Clear();
        Invocations.Clear();
    }

    public void SetInvocationResponse(string method, object? response)
    {
        _invocationResponses[method] = response;
    }

    private object? ResolveInvocationResponse(string method)
    {
        return _invocationResponses.TryGetValue(method, out var value) ? value : null;
    }

    private sealed class RecordingHubClients : IHubClients
    {
        private readonly RecordingRunnerHubContext _context;

        public RecordingHubClients(RecordingRunnerHubContext context)
        {
            _context = context;
        }

        public IClientProxy All => new RecordingClientProxy(_context, "all");
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new RecordingClientProxy(_context, "all-except");
        IClientProxy IHubClients<IClientProxy>.Client(string connectionId) => new RecordingClientProxy(_context, connectionId);
        ISingleClientProxy IHubClients.Client(string connectionId) => new RecordingClientProxy(_context, connectionId);
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new RecordingClientProxy(_context, string.Join(",", connectionIds));
        public IClientProxy Group(string groupName) => new RecordingClientProxy(_context, groupName);
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new RecordingClientProxy(_context, groupName);
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => new RecordingClientProxy(_context, string.Join(",", groupNames));
        public IClientProxy User(string userId) => new RecordingClientProxy(_context, userId);
        public IClientProxy Users(IReadOnlyList<string> userIds) => new RecordingClientProxy(_context, string.Join(",", userIds));
    }

    private sealed class RecordingClientProxy : ISingleClientProxy
    {
        private readonly RecordingRunnerHubContext _context;
        private readonly string _connectionId;

        public RecordingClientProxy(RecordingRunnerHubContext context, string connectionId)
        {
            _context = context;
            _connectionId = connectionId;
        }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            _context.SentMessages.Add(new RecordedRunnerHubMessage(_connectionId, method, args));
            return Task.CompletedTask;
        }

        public Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            _context.Invocations.Add(new RecordedRunnerHubInvocation(_connectionId, method, args));
            var response = _context.ResolveInvocationResponse(method);
            return Task.FromResult(response is T typed ? typed : default(T)!);
        }
    }

    private sealed class NoopGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

public sealed record RecordedRunnerHubMessage(string ConnectionId, string Method, IReadOnlyList<object?> Arguments);

public sealed record RecordedRunnerHubInvocation(string ConnectionId, string Method, IReadOnlyList<object?> Arguments);
