# Review Findings

## P1: Non-interactive `stop --issue` performs HTTP before rejecting confirmation

`packages/cli/Mohist.Cli/MohistCliCommands.Run.cs:525-535` resolves the target with `ResolveRunTargetAsync` before checking `PromptsEnabled` and requiring `--yes`. Therefore `mo run stop --issue 42` in a non-interactive context first sends a GET for issue 42, then exits with the missing-`--yes` error. The run-control acceptance criteria require this invocation to fail without any HTTP request, and the confirmation must be checked before any remote work. Move the non-interactive confirmation validation ahead of target resolution, while preserving the resolved Run ID for the interactive prompt.

## P1: `run view` rejects the required JSON field-selection shape

`packages/cli/Mohist.Cli/MohistCliCommands.Run.Reads.cs:50-52` defines `RunViewDescriptor` with only `status` and `issueRef`, but the run-reads spec scenario and T-002 acceptance criteria exercise `mo run view wr_abc --json id,status,currentStage`. `JsonSelection.Parse` consequently treats `id` and `currentStage` as invalid and the command exits locally without fetching the run. The implementation needs a view output descriptor/projection that supports the accepted run fields and maps them to the actual nested `WorkflowRunDetailDto` payload, while retaining the full default view output.

<promise>FAIL</promise>
