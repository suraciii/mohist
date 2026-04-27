# Merge Conflict Resolution

You are resolving git merge conflicts in a worktree for the Mohist workflow.

## Context

A merge from master into the issue branch produced conflicts. Your job is to resolve every conflict marker so the code compiles and both sides' changes are preserved.

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
3. **TypeScript compilation must pass.** After resolving, run `cd packages/cli && npm run build` to verify the project compiles without errors.
4. **Commit the resolution.** After all conflicts are resolved and compilation passes, stage and commit:
   ```
   git add -A
   git commit -m "resolve merge conflicts"
   ```

## Steps

1. Read each conflict file listed below.
2. For each conflict block, understand what master changed and what the issue changed.
3. Merge both changes intelligently — ordering, combining, or interleaving as appropriate.
4. Remove all conflict markers.
5. Verify TypeScript compilation passes.
6. Commit the resolution.
