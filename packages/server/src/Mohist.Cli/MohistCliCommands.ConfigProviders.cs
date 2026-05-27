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

}
