using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Config;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.SystemInfo;
using Mohist.Server.Workflow.Storage;

namespace Mohist.Server.TestSupport;

internal sealed class InMemoryHostFileDependencies
{
    private readonly InMemoryStorageFileSystem _storage = new();
    private readonly InMemoryConfigFileStore _config = new();
    private readonly InMemorySystemUpdateStore _systemUpdates = new();
    private readonly InMemoryWebContentProvider _webContent = new("<html><body>Mohist Test Web</body></html>");

    public void ReplaceServiceRegistrations(
        IServiceCollection services,
        string configPath,
        string artifactStorageRoot,
        string attachmentStorageRoot,
        TimeProvider timeProvider)
    {
        services.RemoveAll<IWebContentProvider>();
        services.AddSingleton<IWebContentProvider>(_webContent);
        services.RemoveAll<IFileSystem>();
        services.AddSingleton<IFileSystem, EmptyFileSystem>();
        services.RemoveAll<IRuntimeBuildInfo>();
        services.AddSingleton<IRuntimeBuildInfo>(new FixedRuntimeBuildInfo(timeProvider.GetUtcNow()));
        services.RemoveAll<IProcessStartTimeProvider>();
        services.AddSingleton<IProcessStartTimeProvider>(new FixedProcessStartTimeProvider(timeProvider.GetUtcNow()));
        services.RemoveAll<IServiceStatusChecker>();
        services.AddSingleton<IServiceStatusChecker, NoopServiceStatusChecker>();
        services.RemoveAll<IManagedAssetInspector>();
        services.AddSingleton<IManagedAssetInspector, AvailableManagedAssetInspector>();
        services.RemoveAll<ISystemUpdateStore>();
        services.AddSingleton<ISystemUpdateStore>(_systemUpdates);
        services.RemoveAll<IWorkflowArtifactStorage>();
        services.AddSingleton<IWorkflowArtifactStorage>(provider => new FileSystemWorkflowArtifactStorage(
            artifactStorageRoot,
            provider.GetRequiredService<ILogger<FileSystemWorkflowArtifactStorage>>(),
            _storage));
        services.RemoveAll<IAttachmentStorage>();
        services.AddSingleton<IAttachmentStorage>(provider => new FileSystemAttachmentStorage(
            attachmentStorageRoot,
            provider.GetRequiredService<ILogger<FileSystemAttachmentStorage>>(),
            _storage));
        services.RemoveAll<ConfigService>();
        services.AddSingleton(provider => new ConfigService(
            provider.GetRequiredService<IConfiguration>(),
            provider.GetRequiredService<IEnvironmentVariableProvider>(),
            provider.GetRequiredService<ILogger<ConfigService>>(),
            configPath,
            _config));
    }
}
