namespace Mohist.Cli;

internal static class CommandHelpTopics
{
    public static IReadOnlyCollection<string> Names { get; } =
        new[] { "output", "environment", "exit-codes" };

    public static bool TryGet(string name, out string body) =>
        Topics.TryGetValue(name, out body!);

    private static readonly IReadOnlyDictionary<string, string> Topics = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["output"] =
            """
            OUTPUT

                mo prints human-readable tables by default. Resource commands expose
                field selection through a single --json flag; --json with no value
                lists the available fields without making the request, while --json a,b
                prints only the selected fields and skips the request when the field
                list is invalid.

                Result data goes to stdout. Diagnostics, usage failures, and parser
                errors go to stderr. Scripts should consume stdout and surface stderr
                separately; the CLI never interleaves progress text with the result.

                For full per-command field lists, run `mo <command> --help`.
            """,
        ["environment"] =
            """
            ENVIRONMENT

                mo reads the following variables; none of them require a value to
                exist before the CLI can run, and no environment variable is read
                when a command operates purely on local files.

                MO_PROJECTS_DIR
                    Override the directory the CLI uses to discover the active
                    Project when --project is not supplied.

                MOHIST_PROMPT_DISABLED
                    When set to 1, the CLI treats every prompt as declined and
                    requires --yes for irreversible actions such as `mo run stop`.

                MOHIST_HTTP_TIMEOUT / MOHIST_API_BASE_URL
                    Override the Server timeout or base URL; intended for tests and
                    self-hosted deployments only.

                Long text and documents go through stdin via `mo <command> ... --<name>-file -`
                or `--file -`; the CLI does not read other interactive prompts.
            """,
        ["exit-codes"] =
            """
            EXIT CODES

                0    success — the command completed and stdout holds the result
                1    operation failure — the command understood the request but
                     the action could not complete (Server rejection, missing
                     resource, invalid input that depends on runtime state)
                2    usage failure — the command rejected the invocation; the
                     CLI prints the nearest usage to stderr and no remote
                     request is made
                130  cancelled — the operation was interrupted

                Operation failures print the object, the rejection reason, and a
                stable Server error code on stderr. A `hint:` line appears only
                when a certain recovery exists; speculative hints are never
                appended.

                For per-command exit behavior, run `mo <command> --help`.
            """,
    };
}