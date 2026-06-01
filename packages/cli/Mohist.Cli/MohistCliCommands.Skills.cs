using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Mohist.Cli;

internal static class SkillsCommands
{
    public static Command Build(IServiceProvider provider)
    {
        var skills = new Command("skills", "Manage coder agent skills");
        var assets = provider.GetRequiredService<SkillAssetService>();
        var api = provider.GetRequiredService<MohistCliApi>();

        skills.Subcommands.Add(BuildInstall(provider));
        skills.Subcommands.Add(BuildList(assets, api.Output));
        skills.Subcommands.Add(BuildGet(assets, api.Output, api.Error));
        skills.Subcommands.Add(BuildPath(assets, api.Output, api.Error));

        return skills;
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

    private static Command BuildList(SkillAssetService assets, TextWriter output)
    {
        var list = new Command("list", "List Mohist built-in coder agent skills");
        var jsonOption = new Option<bool>("--json") { Description = "Output JSON" };
        list.Options.Add(jsonOption);
        list.SetAction(async ctx =>
        {
            var json = ctx.GetValue(jsonOption);
            var skills = assets.ListVisibleSkills();
            if (json)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(skills));
                return 0;
            }

            foreach (var skill in skills)
                await output.WriteLineAsync($"{skill.Name}\t{skill.Description}");

            return 0;
        });

        return list;
    }

    private static Command BuildGet(SkillAssetService assets, TextWriter output, TextWriter error)
    {
        var get = new Command("get", "Print packaged Mohist coder agent skill guidance");
        var nameArgument = new Argument<string?>("name") { Arity = ArgumentArity.ZeroOrOne, Description = "Built-in skill name" };
        var fullOption = new Option<bool>("--full") { Description = "Append packaged references and templates" };
        var jsonOption = new Option<bool>("--json") { Description = "Output JSON" };
        var allOption = new Option<bool>("--all") { Description = "Print all visible built-in skills" };

        get.Arguments.Add(nameArgument);
        get.Options.Add(fullOption);
        get.Options.Add(jsonOption);
        get.Options.Add(allOption);
        get.SetAction(async ctx =>
        {
            var name = ctx.GetValue(nameArgument);
            var full = ctx.GetValue(fullOption);
            var json = ctx.GetValue(jsonOption);
            var all = ctx.GetValue(allOption);

            if (all)
            {
                var skills = new List<object>();
                foreach (var metadata in assets.ListVisibleSkills())
                {
                    var result = assets.GetSkill(metadata.Name, full);
                    if (!result.Found || result.Skill is null)
                    {
                        await error.WriteLineAsync(result.Error ?? $"Unable to resolve built-in skill '{metadata.Name}'.");
                        return 1;
                    }

                    if (json)
                    {
                        skills.Add(new
                        {
                            name = result.Skill.Name,
                            description = result.Skill.Description,
                            content = BuildSkillOutput(result.Skill, full),
                        });
                        continue;
                    }

                    await WriteAllSkillTextAsync(output, result.Skill, full, includeSeparator: skills.Count > 0);
                    skills.Add(result.Skill.Name);
                }

                if (json)
                    await output.WriteLineAsync(JsonSerializer.Serialize(skills));

                return 0;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                await error.WriteLineAsync("A built-in skill name is required unless --all is specified.");
                return 1;
            }

            var skillResult = assets.GetSkill(name, full);
            if (!skillResult.Found || skillResult.Skill is null)
            {
                await error.WriteLineAsync(skillResult.Error ?? $"Unable to resolve built-in skill '{name}'.");
                return 1;
            }

            if (json)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    name = skillResult.Skill.Name,
                    description = skillResult.Skill.Description,
                    content = BuildSkillOutput(skillResult.Skill, full),
                }));
                return 0;
            }

            await output.WriteAsync(BuildSkillOutput(skillResult.Skill, full));
            return 0;
        });

        return get;
    }

    private static Command BuildPath(SkillAssetService assets, TextWriter output, TextWriter error)
    {
        var path = new Command("path", "Print the packaged path for a Mohist built-in skill");
        var nameArgument = new Argument<string>("name") { Description = "Built-in skill name" };
        var jsonOption = new Option<bool>("--json") { Description = "Output JSON" };

        path.Arguments.Add(nameArgument);
        path.Options.Add(jsonOption);
        path.SetAction(async ctx =>
        {
            var name = ctx.GetValue(nameArgument)!;
            var json = ctx.GetValue(jsonOption);
            var result = assets.GetSkill(name, includeSupplementaryFiles: false);
            if (!result.Found || result.Skill is null)
            {
                await error.WriteLineAsync(result.Error ?? $"Unable to resolve built-in skill '{name}'.");
                return 1;
            }

            if (json)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new { name = result.Skill.Name, path = result.Skill.DirectoryPath }));
                return 0;
            }

            await output.WriteLineAsync(result.Skill.DirectoryPath);
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
