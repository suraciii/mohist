# Review

No merge-blocking findings.

The implementation removes the steady-state workflow-timeline interval, wires task and artifact events into the existing issue-query invalidation path, exposes the reconnect version through `LiveTaskState`, and performs a page-owned catch-up refetch after reconnect. The attention nudge is edge-triggered from the derived runtime summary, avoids mount-time notifications, resets on issue navigation, and leaves the viewed-issue global-toast suppression intact.

The existing page composition retains the first-load-only loading guard and stable component/list identities, so live query updates do not remount the reading flow or collapsible rail state. The same data and toast paths are used for narrow viewports.

Verification completed:

- `npm run typecheck -w packages/web`
- `npm run test:run -w packages/web` (376 files, 5062 tests)
- `git diff --check`

<promise>PASS</promise>
