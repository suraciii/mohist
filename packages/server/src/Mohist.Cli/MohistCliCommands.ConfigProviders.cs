using System.CommandLine;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

internal static class ConfigProvidersCommands
{
    public static Command BuildConfig(MohistCliApi api)
    {
        var config = new Command("config", "Configuration management");

        config.Subcommands.Add(BuildConfigList(api));
        config.Subcommands.Add(BuildConfigGet(api));
        config.Subcommands.Add(BuildConfigSet(api));

        return config;
    }

    public static Command BuildProviders(MohistCliApi api)
    {
        var providers = new Command("providers", "AI provider management");
        providers.Aliases.Add("provider");

        providers.Subcommands.Add(BuildProvidersList(api));
        providers.Subcommands.Add(BuildProvidersModels(api));
        providers.Subcommands.Add(BuildProvidersRuntime(api));
        providers.Subcommands.Add(BuildProvidersSave(api));
        providers.Subcommands.Add(BuildProvidersTest(api));
        providers.Subcommands.Add(BuildProvidersDelete(api));

        return providers;
    }

    private static Command BuildConfigList(MohistCliApi api)
    {
        var cmd = new Command("list", "List all config entries");
        cmd.SetAction((ParseResult _) => api.PrintGetAsync("/api/config/list"));
        return cmd;
    }

    private static Command BuildConfigGet(MohistCliApi api)
    {
        var cmd = new Command("get", "Get config value");
        var keyArg = new Argument<string>("key") { Description = "Config key" };
        cmd.Arguments.Add(keyArg);
        cmd.SetAction(ctx =>
        {
            var key = ctx.GetValue(keyArg);
            return api.PrintGetAsync($"/api/config/{MohistCliCommands.Escape(key!)}");
        });
        return cmd;
    }

    private static Command BuildConfigSet(MohistCliApi api)
    {
        var cmd = new Command("set", "Set config value");
        var keyArg = new Argument<string>("key") { Description = "Config key" };
        var valueArg = new Argument<string>("value") { Description = "Config value" };
        cmd.Arguments.Add(keyArg);
        cmd.Arguments.Add(valueArg);
        cmd.SetAction(ctx =>
        {
            var key = ctx.GetValue(keyArg);
            var value = ctx.GetValue(valueArg);
            return api.PrintPutAsync($"/api/config/{MohistCliCommands.Escape(key!)}", new { value });
        });
        return cmd;
    }

    private static Command BuildProvidersList(MohistCliApi api)
    {
        var cmd = new Command("list", "List all providers");
        cmd.Aliases.Add("ls");
        cmd.SetAction((ParseResult _) => api.PrintGetAsync("/api/providers"));
        return cmd;
    }

    private static Command BuildProvidersModels(MohistCliApi api)
    {
        var cmd = new Command("models", "List available models");
        cmd.SetAction((ParseResult _) => api.PrintGetAsync("/api/providers/models"));
        return cmd;
    }

    private static Command BuildProvidersRuntime(MohistCliApi api)
    {
        var cmd = new Command("runtime", "List runtime providers");
        cmd.SetAction((ParseResult _) => api.PrintGetAsync("/api/providers/runtime"));
        return cmd;
    }

    private static Command BuildProvidersSave(MohistCliApi api)
    {
        var cmd = new Command("save", "Save a provider");
        var idArg = new Argument<string>("id") { Description = "Provider ID" };
        var nameOpt = new Option<string?>("--name") { Description = "Provider name" };
        var apiKeyOpt = new Option<string?>("--api-key", "--key") { Description = "API key" };
        var baseUrlOpt = new Option<string?>("--base-url") { Description = "Base URL" };
        var modelOpt = new Option<string[]?>("--model")
        {
            Description = "Model names",
            AllowMultipleArgumentsPerToken = true,
        };
        var sdkOpt = new Option<string?>("--sdk") { Description = "SDK type" };
        cmd.Arguments.Add(idArg);
        cmd.Options.Add(nameOpt);
        cmd.Options.Add(apiKeyOpt);
        cmd.Options.Add(baseUrlOpt);
        cmd.Options.Add(modelOpt);
        cmd.Options.Add(sdkOpt);
        cmd.SetAction(ctx =>
        {
            var id = ctx.GetValue(idArg);
            var name = ctx.GetValue(nameOpt);
            var apiKey = ctx.GetValue(apiKeyOpt);
            var baseUrl = ctx.GetValue(baseUrlOpt);
            var models = ctx.GetValue(modelOpt);
            var sdk = ctx.GetValue(sdkOpt);
            return api.PrintPostAsync($"/api/providers/{MohistCliCommands.Escape(id!)}", new
            {
                name,
                apiKey,
                baseURL = baseUrl,
                models,
                sdk,
            });
        });
        return cmd;
    }

    private static Command BuildProvidersTest(MohistCliApi api)
    {
        var cmd = new Command("test", "Test a provider connection");
        var nameOpt = new Option<string?>("--name") { Description = "Provider name" };
        var apiKeyOpt = new Option<string?>("--api-key", "--key") { Description = "API key" };
        var baseUrlOpt = new Option<string?>("--base-url") { Description = "Base URL" };
        var modelOpt = new Option<string[]?>("--model")
        {
            Description = "Model names",
            AllowMultipleArgumentsPerToken = true,
        };
        var sdkOpt = new Option<string?>("--sdk") { Description = "SDK type" };
        cmd.Options.Add(nameOpt);
        cmd.Options.Add(apiKeyOpt);
        cmd.Options.Add(baseUrlOpt);
        cmd.Options.Add(modelOpt);
        cmd.Options.Add(sdkOpt);
        cmd.SetAction(ctx =>
        {
            var name = ctx.GetValue(nameOpt);
            var apiKey = ctx.GetValue(apiKeyOpt);
            var baseUrl = ctx.GetValue(baseUrlOpt);
            var models = ctx.GetValue(modelOpt);
            var sdk = ctx.GetValue(sdkOpt);
            return api.PrintPostAsync("/api/providers/test", new
            {
                name,
                apiKey,
                baseURL = baseUrl,
                models,
                sdk,
            });
        });
        return cmd;
    }

    private static Command BuildProvidersDelete(MohistCliApi api)
    {
        var cmd = new Command("delete", "Delete a provider");
        cmd.Aliases.Add("remove");
        cmd.Aliases.Add("rm");
        var idArg = new Argument<string>("id") { Description = "Provider ID" };
        cmd.Arguments.Add(idArg);
        cmd.SetAction(ctx =>
        {
            var id = ctx.GetValue(idArg);
            return api.PrintDeleteAsync($"/api/providers/{MohistCliCommands.Escape(id!)}");
        });
        return cmd;
    }
}