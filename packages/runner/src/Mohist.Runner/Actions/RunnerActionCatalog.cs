using Mohist.Runner.Handlers;
using Mohist.Runner.Transport;

namespace Mohist.Runner.Actions;

public static class RunnerActionCatalog
{
    public const string Agent = "mohist/coder-agent";
    public const string AiReview = "mohist/check/ai-review";

    public const string CoreProcess = "core/process";
    public const string CoreScript = "core/script";
    public const string CoreArtifactExists = "core/artifact-exists";
    public const string CoreMarker = "core/marker";

    public const string MohistOpenSpecTasks = "mohist/openspec-tasks";
    public const string MohistMergeReady = "mohist/merge-ready";
    public const string MohistRebase = "mohist/rebase";
    public const string MohistRebaseStatus = "mohist/rebase-status";
    public const string MohistOpenSpecSync = "mohist/openspec-sync";
    public const string MohistArchiveChange = "mohist/archive-change";
    public const string MohistMerge = "mohist/merge";

    public static void RegisterDefaults(ActionManager manager, IServiceProvider services)
    {
        manager.Register(Agent, () => new AgentAction(
            services.GetRequiredService<IAgentExecutor>(),
            services.GetRequiredService<ISessionTelemetrySink>(),
            services.GetRequiredService<IAgentCompletionVerifier>(),
            services.GetRequiredService<IAgentSessionRepairer>()));
        manager.Register(AiReview, () => new AiReviewAction(services.GetRequiredService<IAgentExecutor>()));

        manager.Register(CoreProcess, () => new ProcessHandler(services.GetRequiredService<ILogger<ProcessHandler>>()));
        manager.Register(CoreScript, () => new ScriptHandler(services.GetRequiredService<ILogger<ScriptHandler>>()));
        manager.Register(CoreArtifactExists, () => new ArtifactExistsAction());
        manager.Register(CoreMarker, () => new MarkerAction());

        manager.Register(MohistOpenSpecTasks, () => new OpenSpecTasksAction());
        manager.Register(MohistMergeReady, () => new MergeReadyAction());
        manager.Register(MohistRebase, () => new RebaseAction());
        manager.Register(MohistRebaseStatus, () => new RebaseStatusAction());
        manager.Register(MohistOpenSpecSync, () => new OpenSpecSyncAction());
        manager.Register(MohistArchiveChange, () => new ArchiveChangeAction());
        manager.Register(MohistMerge, () => new MergeAction());

    }
}
