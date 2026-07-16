namespace Mohist.Server.Infrastructure.Config;

public static class MohistConfigPath
{
    private const string HomeEnvironmentVariable = "HOME";

    public static string Resolve(IEnvironmentVariableProvider environment)
    {
        var home = environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Path.Combine(home, ".mohist", "config.jsonc");
    }
}
