using System.CommandLine;
using System.CommandLine.Parsing;

namespace Mohist.Cli;

internal static class EventCommands
{
    public static Command Build(MohistCliApi api)
    {
        var events = new Command("event", "Event delivery operations");
        var deadLetter = new Command("dead-letter", "Inspect and recover dead-lettered event deliveries");
        deadLetter.Subcommands.Add(BuildList(api));
        deadLetter.Subcommands.Add(BuildRedeliver(api));
        events.Subcommands.Add(deadLetter);
        return events;
    }

    private static Command BuildList(MohistCliApi api)
    {
        var cmd = new Command("list", "List unresolved dead-lettered event deliveries");
        var handlerOpt = new Option<string?>("--handler") { Description = "Filter by failing handler full name" };
        var limitOpt = new Option<int>("--limit")
        {
            Description = "Maximum rows to return (1-500)",
            DefaultValueFactory = _ => 100,
        };
        var outputOpt = MohistCliCommands.OutputOption(defaultValue: "table");
        cmd.Options.Add(handlerOpt);
        cmd.Options.Add(limitOpt);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var handler = ctx.GetValue(handlerOpt);
            var limit = ctx.GetValue(limitOpt);
            var output = ctx.GetValue(outputOpt);
            if (limit is < 1 or > 500)
            {
                api.Error.WriteLine("--limit must be between 1 and 500");
                return Task.FromResult(1);
            }

            var (mode, exit) = api.ResolveOutputMode(output);
            if (exit != 0)
                return Task.FromResult(exit);

            var query = $"?limit={limit}";
            if (!string.IsNullOrWhiteSpace(handler))
                query += $"&handler={MohistCliCommands.Escape(handler)}";
            return api.PrintWithOutputAsync(
                $"/api/events/dead-letters{query}",
                mode,
                nameof(MohistCliApi.TableShape.DeadLetterList));
        });
        return cmd;
    }

    private static Command BuildRedeliver(MohistCliApi api)
    {
        var cmd = new Command("redeliver", "Retry the failing handler recorded by a dead-letter row");
        var idArg = new Argument<long>("id") { Description = "Dead-letter id" };
        var outputOpt = MohistCliCommands.OutputOption(defaultValue: "table");
        cmd.Arguments.Add(idArg);
        cmd.Options.Add(outputOpt);
        cmd.SetAction(ctx =>
        {
            var id = ctx.GetValue(idArg);
            var output = ctx.GetValue(outputOpt);
            if (id <= 0)
            {
                api.Error.WriteLine("id must be positive");
                return Task.FromResult(1);
            }

            var (mode, exit) = api.ResolveOutputMode(output);
            if (exit != 0)
                return Task.FromResult(exit);

            return api.PrintPostWithOutputAsync(
                $"/api/events/dead-letters/{id}/redeliver",
                new { },
                mode,
                nameof(MohistCliApi.TableShape.DeadLetterRedelivery));
        });
        return cmd;
    }
}
