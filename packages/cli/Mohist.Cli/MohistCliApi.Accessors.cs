namespace Mohist.Cli;

internal sealed partial class MohistCliApi
{
    internal IFileSystem FileSystem => _fileSystem;
    internal ICommandExecutor CommandExecutor => _commandExecutor;
    internal TextReader StandardInput => _standardInput;
    internal CliResponseReader ResponseReader => _responseReader;
    internal Func<string> GetUserHome => _getUserHome;
    internal TimeProvider TimeProvider => _timeProvider;
    internal string CurrentProjectStatePath => ProjectReferenceResolver.StatePath(_fileSystem.CurrentDirectory);
}
