using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace Mohist.Server.Infrastructure.Config;

public static class MohistConfigurationExtensions
{
    public static IConfigurationBuilder AddMohistConfigFile(
        this IConfigurationBuilder builder,
        string? path = null,
        bool optional = true,
        bool reloadOnChange = true)
    {
        var configPath = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mohist",
            "config.jsonc");

        if (!File.Exists(configPath))
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
            source.Path = configPath;
            source.Optional = optional;
            source.ReloadOnChange = reloadOnChange;
            source.ResolveFileProvider();
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
}
