## Summary

<!--
What changes and why. Start from the behavior or problem this PR addresses;
do not list files one by one. State key decisions and trade-offs. If the PR
comes from the Mohist pipeline (branch mo/issue-N, title = issue title), keep
both.
-->

<What changes and why>

## Related issue

<!-- Fixes #N to auto-close, or delete this section when none. Before opening:
search existing issues/PRs for duplicates, link the closest one, or say
"none found". -->

## Spec basis

<!--
Link the docs/ or design/ documents this change implements. If the change is
not covered by any spec, say why it is still right. When spec and
implementation diverge, state the gap here and in the document.
-->

<Spec documents, or why none applies>

## Breaking changes & migration

<!--
Write "None" when there are none. Otherwise state what breaks, who is
affected, and how to migrate (config, data, API, CLI).
-->

<None, or what breaks and how to migrate>

## User-facing changes

<!--
Write "None" when there is no user-visible change. Otherwise one sentence
ready for release notes.
-->

```release-note
<None, or one sentence for users>
```

## Verification

<!--
Actual commands run, environment, and results (exit codes / test summaries).
UI changes include before/after screenshots. Performance changes include
baseline and result.
-->

<Commands + environment + results>

## Checklist

- [ ] Build passes and tests are green locally (`npm run build`, `npm test`)
- [ ] No old and new tests coexist for the same behavior
- [ ] No real time in production code (TimeProvider / fake timers injected)
- [ ] Spec supports the change; stated gaps when not

---

<!-- Merged with squash — the PR title becomes the commit on main. Use
Conventional Commits: feat(scope): subject, fix(scope): subject, ... -->
