# Merge Conflict Resolution

You are resolving git merge conflicts in a worktree for the Mohist workflow.

## Context

A rebase from master into the issue branch produced conflicts. Your job is to resolve ALL conflict markers so the rebase completes fully.

## Conflict Markers

Files contain markers in this format:

```
<<<<<<< HEAD
(master changes)
=======
(issue changes)
>>>>>>> mo/issue-N
```

- `<<<<<<< HEAD` to `=======`: changes from **master** (the base branch)
- `=======` to `>>>>>>> mo/issue-N`: changes from the **issue branch**

## Resolution Rules

1. **Preserve both sides.** Never drop or overwrite either side's changes. Integrate them so both sets of modifications are present in the final code.
2. **Resolve every conflict.** Search for `<<<<<<<`, `=======`, and `>>>>>>>` across all conflict files. No markers may remain.
3. **TypeScript compilation must pass.** After all conflicts are resolved, run `cd packages/cli && npm run build` to verify the project compiles without errors.
4. **Commit each resolution.** After resolving conflicts for the current commit, stage and commit:
   ```
   git add -A
   git commit -m "resolve merge conflicts"
   ```

## Steps — REPEAT UNTIL REBASE IS FULLY COMPLETE

The rebase may have conflicts in MULTIPLE commits. You MUST loop until the entire rebase finishes:

1. Read each conflict file listed below.
2. For each conflict block, understand what master changed and what the issue changed.
3. Merge both changes intelligently — ordering, combining, or interleaving as appropriate.
4. Remove all conflict markers.
5. Stage and commit the resolution: `git add -A && git commit -m "resolve merge conflicts"`
6. Continue the rebase: `git rebase --continue`
7. Check the exit code:
   - If **succeeded** (no error) — the rebase is complete, you are done. Run a final build verification: `cd packages/cli && npm run build`
   - If **failed** (more conflicts) — go back to step 1 and resolve the new conflicts. Do NOT stop.

**IMPORTANT:** Do NOT stop after resolving the first round of conflicts. Keep looping until `git rebase --continue` succeeds without error.
