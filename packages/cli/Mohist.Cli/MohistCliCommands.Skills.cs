using System.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Mohist.Cli;

internal static class SkillCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Command Build(IServiceProvider provider)
    {
        var skill = new Command("skill", "Manage coder agent skills");
        var assets = provider.GetRequiredService<SkillAssetService>();
        var api = provider.GetRequiredService<MohistCliApi>();

        skill.Subcommands.Add(BuildInstall(provider));
        skill.Subcommands.Add(BuildList(assets, api));
        skill.Subcommands.Add(BuildView(assets, api));
        skill.Subcommands.Add(BuildPath(assets, api));
        skill.Subcommands.Add(BuildSync(provider));

        return skill;
    }

    private static Command BuildInstall(IServiceProvider provider)
    {
        var install = new Command("install", "Install Mohist built-in coder agent skills");
        var installer = provider.GetRequiredService<SkillInstallService>();
        var pathOption = new Option<string?>("--path") { Description = "Repository path for OpenCode skill stubs" };
        var claudeOption = new Option<bool>("--claude") { Description = "Install discovery stubs under .claude/skills" };
        var hermesOption = new Option<bool>("--hermes") { Description = "Install full packaged skills under ${HERMES_HOME:-~/.hermes}/skills" };

        install.Options.Add(pathOption);
        install.Options.Add(claudeOption);
        install.Options.Add(hermesOption);
        install.SetAction(async ctx =>
        {
            var targetPath = ctx.GetValue(pathOption);
            var claude = ctx.GetValue(claudeOption);
            var hermes = ctx.GetValue(hermesOption);
            var exitCode = await installer.InstallAsync(new SkillInstallOptions(targetPath, claude, hermes));
            return exitCode;
        });

        return install;
    }

    private static Command BuildSync(IServiceProvider provider)
    {
        var sync = new Command("sync", "Sync working-tree skill-data into the managed cache so `mo skill view` reflects local edits");
        var updater = MohistCliCommands.ResolveSourceCodeUpdater(provider);
        var repoRootOpt = new Option<string?>("--repo-root") { Description = "Repository root path" };
        var sourceOpt = new Option<string?>("--source") { Description = "Source skill-data directory (default: <repo-root>/packages/cli/Mohist.Cli/skill-data)" };
        var dryRunOpt = MohistCliCommands.DryRunOption();

        sync.Options.Add(repoRootOpt);
        sync.Options.Add(sourceOpt);
        sync.Options.Add(dryRunOpt);
        sync.SetAction(async (ctx, token) =>
        {
            var repoRoot = ctx.GetValue(repoRootOpt);
            var source = ctx.GetValue(sourceOpt);
            var dryRun = ctx.GetValue(dryRunOpt);
            return await updater.SyncSkillsAsync(repoRoot, source, dryRun, token);
        });
        return sync;
    }

    private static Command BuildList(SkillAssetService assets, MohistCliApi api)
    {
        var list = new Command("list", "List Mohist built-in coder agent skills");
        var jsonOption = MohistCliCommands.JsonSelectionOption();
        var descriptor = new ResourceDescriptor(ResourceCardinality.Collection, ["name", "description"]);
        list.Options.Add(jsonOption);
        list.SetAction(async ctx =>
        {
            var selection = JsonSelection.Parse(descriptor, ctx.GetResult(jsonOption) is not null, ctx.GetValue(jsonOption));
            var skills = assets.ListVisibleSkills();
            if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                return api.WriteJsonSelectionResult(descriptor, selection);
            if (selection.Kind == JsonSelectionKind.Selected)
                return await new CliResultWriter(api.Invocation).WriteSuccessAsync(
                    selection.Project(JsonSerializer.SerializeToNode(skills, JsonOptions), descriptor.Cardinality));

            foreach (var skill in skills)
                await api.Output.WriteLineAsync($"{skill.Name}\t{skill.Description}");

            return 0;
        });

        return list;
    }

    private static Command BuildView(SkillAssetService assets, MohistCliApi api)
    {
        var view = new Command("view", "Print packaged Mohist coder agent skill guidance");
        var nameArgument = new Argument<string?>("name") { Arity = ArgumentArity.ZeroOrOne, Description = "Built-in skill name" };
        var fullOption = new Option<bool>("--full") { Description = "Append packaged references and templates" };
        var jsonOption = MohistCliCommands.JsonSelectionOption();
        var allOption = new Option<bool>("--all") { Description = "Print all visible built-in skills" };

        view.Arguments.Add(nameArgument);
        view.Options.Add(fullOption);
        view.Options.Add(jsonOption);
        view.Options.Add(allOption);
        view.SetAction(async ctx =>
        {
            var name = ctx.GetValue(nameArgument);
            var full = ctx.GetValue(fullOption);
            var all = ctx.GetValue(allOption);
            var descriptor = new ResourceDescriptor(
                all ? ResourceCardinality.Collection : ResourceCardinality.Single,
                ["name", "description", "content"]);
            var selection = JsonSelection.Parse(descriptor, ctx.GetResult(jsonOption) is not null, ctx.GetValue(jsonOption));
            if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                return api.WriteJsonSelectionResult(descriptor, selection);

            if (all)
            {
                var skills = new List<object>();
                foreach (var metadata in assets.ListVisibleSkills())
                {
                    var result = assets.GetSkill(metadata.Name, full);
                    if (!result.Found || result.Skill is null)
                    {
                        await api.Error.WriteLineAsync(result.Error ?? $"Unable to resolve built-in skill '{metadata.Name}'.");
                        return 1;
                    }

                    if (selection.Kind == JsonSelectionKind.Selected)
                    {
                        skills.Add(new
                        {
                            name = result.Skill.Name,
                            description = result.Skill.Description,
                            content = BuildSkillOutput(result.Skill, full),
                        });
                        continue;
                    }

                    await WriteAllSkillTextAsync(api.Output, result.Skill, full, includeSeparator: skills.Count > 0);
                    skills.Add(result.Skill.Name);
                }

                if (selection.Kind == JsonSelectionKind.Selected)
                    return await new CliResultWriter(api.Invocation).WriteSuccessAsync(
                        selection.Project(JsonSerializer.SerializeToNode(skills, JsonOptions), descriptor.Cardinality));

                return 0;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                await api.Error.WriteLineAsync("A built-in skill name is required unless --all is specified.");
                return 1;
            }

            var skillResult = assets.GetSkill(name, full);
            if (!skillResult.Found || skillResult.Skill is null)
            {
                await api.Error.WriteLineAsync(skillResult.Error ?? $"Unable to resolve built-in skill '{name}'.");
                return 1;
            }

            if (selection.Kind == JsonSelectionKind.Selected)
            {
                return await new CliResultWriter(api.Invocation).WriteSuccessAsync(selection.Project(JsonSerializer.SerializeToNode(new
                {
                    name = skillResult.Skill.Name,
                    description = skillResult.Skill.Description,
                    content = BuildSkillOutput(skillResult.Skill, full),
                }, JsonOptions), descriptor.Cardinality));
            }

            await api.Output.WriteAsync(BuildSkillOutput(skillResult.Skill, full));
            return 0;
        });

        return view;
    }

    private static Command BuildPath(SkillAssetService assets, MohistCliApi api)
    {
        var path = new Command("path", "Print the packaged path for a Mohist built-in skill");
        var nameArgument = new Argument<string>("name") { Description = "Built-in skill name" };
        var jsonOption = MohistCliCommands.JsonSelectionOption();
        var descriptor = new ResourceDescriptor(ResourceCardinality.Single, ["name", "path"]);

        path.Arguments.Add(nameArgument);
        path.Options.Add(jsonOption);
        path.SetAction(async ctx =>
        {
            var name = ctx.GetValue(nameArgument)!;
            var selection = JsonSelection.Parse(descriptor, ctx.GetResult(jsonOption) is not null, ctx.GetValue(jsonOption));
            if (selection.Kind is JsonSelectionKind.Discovery or JsonSelectionKind.Invalid)
                return api.WriteJsonSelectionResult(descriptor, selection);
            var result = assets.GetSkill(name, includeSupplementaryFiles: false);
            if (!result.Found || result.Skill is null)
            {
                await api.Error.WriteLineAsync(result.Error ?? $"Unable to resolve built-in skill '{name}'.");
                return 1;
            }

            if (selection.Kind == JsonSelectionKind.Selected)
            {
                return await new CliResultWriter(api.Invocation).WriteSuccessAsync(
                    selection.Project(JsonSerializer.SerializeToNode(new { name = result.Skill.Name, path = result.Skill.DirectoryPath }, JsonOptions), descriptor.Cardinality));
            }

            await api.Output.WriteLineAsync(result.Skill.DirectoryPath);
            return 0;
        });

        return path;
    }

    private static async Task WriteAllSkillTextAsync(TextWriter output, BuiltInSkillContent skill, bool includeSupplementaryFiles, bool includeSeparator)
    {
        if (includeSeparator)
            await output.WriteLineAsync();

        await output.WriteLineAsync($"## {skill.Name}");
        await output.WriteAsync(BuildSkillOutput(skill, includeSupplementaryFiles));
    }

    private static string BuildSkillOutput(BuiltInSkillContent skill, bool includeSupplementaryFiles)
    {
        if (!includeSupplementaryFiles)
            return skill.SkillMarkdown;

        var buffer = new System.Text.StringBuilder(skill.SkillMarkdown);
        foreach (var file in skill.SupplementaryFiles)
        {
            if (buffer.Length > 0 && buffer[^1] != '\n')
                buffer.AppendLine();

            if (buffer.Length == 0 || buffer[^1] != '\n')
                buffer.AppendLine();

            buffer.AppendLine($"--- {file.RelativePath} ---");
            buffer.Append(file.Content);
            if (buffer.Length > 0 && buffer[^1] != '\n')
                buffer.AppendLine();
        }

        return buffer.ToString();
    }
}