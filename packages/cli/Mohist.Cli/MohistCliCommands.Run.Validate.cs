using Mohist.Workflow.Definition;
using System.CommandLine;

namespace Mohist.Cli;

internal static partial class RunCommands
{
    private static Command BuildValidate(MohistCliApi api)
    {
        var cmd = new Command("validate", "Validate a local Workflow Definition without contacting a server");
        var fileOpt = new Option<string>("--file")
        {
            Description = "Workflow Definition file path, or - to read from standard input",
            Arity = ArgumentArity.ExactlyOne,
        };
        cmd.Options.Add(fileOpt);
        cmd.SetAction(ctx => ValidateAsync(ctx.GetValue(fileOpt)));
        return cmd;

        async Task<int> ValidateAsync(string? file)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                api.Error.WriteLine("--file is required and must not be empty");
                return 1;
            }

            string yaml;
            if (file == "-")
            {
                yaml = await api.StandardInput.ReadToEndAsync().ConfigureAwait(false);
            }
            else
            {
                try
                {
                    yaml = await api.FileSystem.ReadAllTextAsync(file).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    api.Error.WriteLine($"could not read Workflow Definition file: {file} ({ex.Message})");
                    return 1;
                }
            }

            var result = WorkflowDefinitionParser.Parse(yaml);
            if (!result.IsValid)
            {
                foreach (var error in result.Errors)
                    api.Error.WriteLine($"{error.Path}: {error.Message}");
                return 1;
            }

            api.Output.WriteLine("Workflow Definition is valid.");
            return 0;
        }
    }
}
