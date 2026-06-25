namespace Mohist.Cli;

internal static class InfoSourcePathResolver
{
    internal static string? ResolveSourcePath(SystemdUnitParser.SystemdUnitFields unit)
    {
        if (!string.IsNullOrWhiteSpace(unit.WorkingDirectory))
            return unit.WorkingDirectory;

        if (!string.IsNullOrWhiteSpace(unit.ExecStart))
        {
            var tokens = InfoExecStartTokenizer.Tokenize(unit.ExecStart!);
            var fromProject = InfoSourceProjectPathResolver.ExtractProjectPath(tokens);
            if (!string.IsNullOrWhiteSpace(fromProject))
                return fromProject;

            var fromBinary = InfoSourceBinaryPathResolver.ExtractBinaryDirectory(tokens);
            if (!string.IsNullOrWhiteSpace(fromBinary))
                return fromBinary;
        }
        return null;
    }
}
