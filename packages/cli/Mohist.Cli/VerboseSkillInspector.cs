namespace Mohist.Cli;

internal sealed class VerboseSkillInspector
{
    private readonly SkillAssetService? _skillAssetService;

    public VerboseSkillInspector(SkillAssetService? skillAssetService)
    {
        _skillAssetService = skillAssetService;
    }

    internal async Task<InfoVerboseSkills> GetSkillsVerboseAsync()
    {
        try
        {
            if (_skillAssetService is null)
                return await Task.FromResult(new InfoVerboseSkills(Array.Empty<InfoVerboseSkill>(), Resolved: true));

            var assets = _skillAssetService.ListVisibleSkills();
            var skills = new List<InfoVerboseSkill>(assets.Count);
            foreach (var asset in assets)
            {
                var installPath = TryGetSkillInstallPath(asset.Name);
                skills.Add(new InfoVerboseSkill(asset.Name, installPath));
            }
            return await Task.FromResult(new InfoVerboseSkills(skills, Resolved: true));
        }
        catch
        {
            return new InfoVerboseSkills(Array.Empty<InfoVerboseSkill>(), Resolved: false);
        }
    }

    private string? TryGetSkillInstallPath(string skillName)
    {
        try
        {
            if (_skillAssetService is null)
                return null;
            var result = _skillAssetService.GetSkill(skillName, includeSupplementaryFiles: false);
            if (result.Found && result.Skill is not null)
                return result.Skill.DirectoryPath;
            return null;
        }
        catch
        {
            return null;
        }
    }
}
