using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Infrastructure.Config;

public static class MohistConfigurationExtensions
{
    public static IConfigurationBuilder AddMohistUserConfigFile(
        this IConfigurationBuilder builder,
        IHostEnvironment environment,
        string? path = null,
        bool optional = true,
        bool reloadOnChange = true,
        IFileProvider? fileProvider = null)
    {
        if (environment.IsEnvironment(MohistHostEnvironment.Testing))
            return builder;

        return builder.AddMohistConfigFile(
            path,
            optional,
            reloadOnChange,
            fileProvider,
            SystemEnvironmentVariableProvider.Instance);
    }

    public static IConfigurationBuilder AddMohistConfigFile(
        this IConfigurationBuilder builder,
        string? path = null,
        bool optional = true,
        bool reloadOnChange = true,
        IFileProvider? fileProvider = null,
        IEnvironmentVariableProvider? environment = null)
    {
        var configPath = path ?? MohistConfigPath.Resolve(
            environment ?? SystemEnvironmentVariableProvider.Instance);

        if (fileProvider is null && !File.Exists(configPath))
            return builder;

        // Use the configureSource overload so we have a direct reference to the
        // JsonConfigurationSource and can wire OnLoadException at registration
        // time. Set OnLoadException to ignore failures: a malformed config or a
        // watcher error must never block startup or crash a running server.
        // The framework's JsonConfigurationFileParser already enables
        // CommentHandling = Skip + AllowTrailingCommas, so JSONC
        // (// line comments, /* */ block comments, trailing commas) loads
        // natively without any preprocessing.
        builder.AddJsonFile(source =>
        {
            source.Optional = optional;
            source.ReloadOnChange = reloadOnChange;
            if (fileProvider is null)
            {
                ConfigurePhysicalConfigSource(source, configPath, CreatePollingFileProvider);
            }
            else
            {
                source.Path = configPath;
                source.FileProvider = fileProvider;
            }
            source.OnLoadException = ctx =>
            {
                ctx.Ignore = true;
                Console.Error.WriteLine(
                    $"[mohist-config] Failed to load/reload config file '{ctx.Provider}'; falling back to defaults/last-known-good. Error: {ctx.Exception.Message}");
                // Build-time hook has no logger in scope, so Console.Error is
                // the durable surface; this mirrors the OtelPortBindingLog precedent.
            };
        });

        return builder;
    }

    internal static void ConfigurePhysicalConfigSource(
        JsonConfigurationSource source,
        string configPath,
        Func<string, IFileProvider> createFileProvider)
    {
        var fullPath = Path.GetFullPath(configPath);
        var directory = Path.GetDirectoryName(fullPath)!;

        source.Path = Path.GetFileName(fullPath);
        source.FileProvider = createFileProvider(directory);
    }

    private static IFileProvider CreatePollingFileProvider(string rootPath) =>
        new PhysicalFileProvider(rootPath)
        {
            // Preserve hot reload without recursively registering every directory under ~/.mohist.
            UsePollingFileWatcher = true,
            UseActivePolling = true
        };
}
