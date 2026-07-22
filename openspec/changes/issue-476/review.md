# Review Findings

## P1: Packaged Skill and remaining docs still teach removed control paths

`packages/cli/Mohist.Cli/skill-data/mohist/SKILL.md:65-72,80,143` still tells agents and users to run `mo issue approve/reject/retry/rerun/rerun-from-stage/stop/force-stop/resume`, including the obsolete `force-stop` distinction. Those commands no longer resolve after this change, and issue 476 explicitly requires that the old aliases be removed and that Skill not explain two syntaxes. The same stale commands remain in user-facing guidance under `docs/the-workflow.md:60-61,98-99`, `docs/getting-started.md:135-136`, `docs/troubleshooting.md:35-39,123,154-157`, and `docs/hermes-notifications.md:51`. Update the source guidance to the canonical `mo run` forms, using `--issue` and `--yes` where required, and remove the obsolete `force-stop` workflow.

<promise>FAIL</promise>
