namespace Mohist.Server.Infrastructure.Security.Secrets;

/// <summary>
/// Bound from configuration section <c>Mohist:SecretStore</c>. The
/// master-key file path defaults to <see cref="PhysicalSecretKeyFile.ResolvePath"/>
/// (env <c>MOHIST_SECRET_KEY_PATH</c> or <c>~/.mohist/slack-master.key</c>);
/// the section exists so the path can be pinned in production via the
/// same <c>Mohist:SectionName</c> pattern used elsewhere in the repo.
/// </summary>
public sealed class SecretStoreOptions
{
    public const string SectionName = "Mohist:SecretStore";

    public string? KeyPath { get; set; }
}
