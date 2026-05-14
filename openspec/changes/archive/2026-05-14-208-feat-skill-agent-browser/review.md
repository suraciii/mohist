## Review: Dynamic Skill Loading (Issue #208)

### Summary

The implementation restructures Mohist's skill system from static file-copy to dynamic packaged loading. Four tasks were completed: asset restructuring (T-001), skill data service + CLI commands (T-002), stub-only installer + build packaging (T-003), and regression tests (T-004).

**Typecheck: PASS** | **Tests: 46/46 PASS** | **Build scripts: PASS**

---

### Correctness

#### WARN: Duplicate `issue-templates.md` content

`packages/cli/src/agent-skills/issue-templates.md` (root-level, used by `issue-template-lookup.ts`) and `packages/cli/src/agent-skills/skill-data/mohist/references/issue-templates.md` are identical copies. They are currently in sync but will diverge over time since neither references the other.

**Impact:** Medium — `mo instructions <label>` reads from `issue-templates.md` (root), while `mo skills get mohist --full` reads from `skill-data/mohist/references/issue-templates.md`. Edits to one won't affect the other.

**Suggestion:** Make one the canonical source and symlink/copy at build time. For example, have `issue-template-lookup.ts` read from `skill-data/mohist/references/issue-templates.md`, or have the build script copy from the canonical source to both locations.

#### WARN: Legacy `templates/` directory left in place

`packages/cli/src/agent-skills/templates/mohist.md` and `templates/mohist-explore.md` are no longer referenced by any code. They are dead files that could confuse future contributors.

**Impact:** Low — no functional impact, but maintenance confusion.

**Suggestion:** Remove `templates/` directory in a follow-up cleanup commit, or add a comment/note that these are deprecated.

#### INFO: `readSkillMeta` stub detection uses `filePath.includes('/stubs/')`

`skill-data-service.ts:57` — This works on Linux/macOS but would fail on Windows if the path separator were `\`. Since Mohist targets Linux/macOS (Node.js CLI), this is acceptable.

---

### Complexity

All functions are under 50 lines. `SkillDataService` is 165 lines total with clear separation of concerns. Cyclomatic complexity is well under 10 for all methods.

The deduplication logic in `discoverSkills()` (lines 108-131) is the most complex part — it iterates stubs first, then skill-data, preferring skill-data entries when both exist. The logic is correct and well-structured.

**PASS** — no complexity concerns.

---

### Test Coverage

**46 tests pass** across `skill-dynamic-loading.test.ts` (23 tests) and `shared-agent-skills.test.ts` (23 tests).

Coverage areas:
- `SkillDataService` discovery, content, supplementary files, path resolution ✓
- `MOHIST_SKILLS_DIR` environment override ✓
- Stub-only install (under 50 lines, `hidden: true`, no extra files) ✓
- Compatibility with preexisting full installed skills ✓
- User-authored skill directories remain untouched ✓
- CLI command registration (install, list, get, path) ✓
- `getSharedSkillNames` returns only managed names ✓

**PASS** — good coverage of spec scenarios.

---

### Security

- No injection risks — all file reads use `path.join()` with controlled paths.
- `MOHIST_SKILLS_DIR` environment variable is validated with `fs.existsSync()` before use.
- No secrets or credentials involved.
- `parseFrontmatter` handles malformed input gracefully (returns `null`).

**PASS** — no security concerns.

---

### Spec Compliance

#### cli-interface/spec.md

| Criterion | Status | Evidence |
|-----------|--------|----------|
| `mo skills install` writes `.agents/skills/mohist/SKILL.md` and `.agents/skills/mohist-explore/SKILL.md` as lightweight stubs with `hidden: true` frontmatter | **PASS** | `shared-agent-skills.ts:18-27` defines stubs for both names; `stubs/mohist/SKILL.md` is 16 lines with `hidden: true`; `stubs/mohist-explore/SKILL.md` is 15 lines with `hidden: true`; verified by tests `skill-dynamic-loading.test.ts:150-163` |
| Install to explicit path via `--path <repo>` | **PASS** | `shared-agent-skills.ts:45` uses `options.projectPath || process.cwd()`; tested in `shared-agent-skills.test.ts:107-126` |
| Existing user-authored skills remain untouched | **PASS** | Installer only manages fixed `mohist` and `mohist-explore` names (`SHARED_SKILL_BUNDLES`); tested in `skill-dynamic-loading.test.ts:213-234` |
| Internal `.mohist/skills` untouched | **PASS** | `SkillDataService` resolves from `stubs/` and `skill-data/` under `agent-skills/`, no reference to `.mohist/skills`; `SkillService` is not imported |
| `mo skills list` returns non-hidden built-in skills sorted by name | **PASS** | `discoverSkills()` at `skill-data-service.ts:103-134` returns deduplicated entries sorted by name; skill-data entries have no `hidden` field (defaults to `false`); tested in `skill-dynamic-loading.test.ts:29-41` |
| `mo skills list --json` returns JSON with name and description | **PASS** | `skills.ts:54-62` maps to JSON with name, description, hidden, path, stub; tested in `skill-dynamic-loading.test.ts:267-285` |
| `mo skills get mohist` prints full packaged content, not stub | **PASS** | `getSkillContent()` at `skill-data-service.ts:136-147` prefers `skill-data/` over `stubs/`; verified stub content contains `获取完整指令` while full content does not (`skill-dynamic-loading.test.ts:56-62`) |
| `mo skills get mohist --full` appends supplementary files in sorted order | **PASS** | `collectSupplementary()` at `skill-data-service.ts:87-101` collects from `references/` and `templates/` with sorted entries; tested in `skill-dynamic-loading.test.ts:64-75` |
| `mo skills get --all` returns visible built-in skill set | **PASS** | `skills.ts:80-100` filters hidden skills and retrieves content for each; tested in `skill-dynamic-loading.test.ts:287-296` |
| `mo skills path mohist` prints packaged directory path | **PASS** | `resolveSkillPath()` at `skill-data-service.ts:153-160` returns `skill-data/mohist` path; tested in `skill-dynamic-loading.test.ts:82-92` |
| `MOHIST_SKILLS_DIR` overrides default lookup | **PASS** | `resolveSkillDataRoot()` at `skill-data-service.ts:68-78` checks env var first; tested in `skill-dynamic-loading.test.ts:96-133` |

#### mohist-skill-guidance/spec.md

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Installed stub points to packaged guidance via `mo skills get <name>` | **PASS** | `stubs/mohist/SKILL.md:9-12` contains `mo skills get mohist` instructions; `stubs/mohist-explore/SKILL.md:9-11` contains `mo skills get mohist-explore` |
| Built-in guidance updates without reinstalling | **PASS** | `getSkillContent()` reads from packaged `skill-data/` directory, not from repository-local stubs; verified by test `skill-dynamic-loading.test.ts:187-195` |
| `--full` includes supplementary reference content | **PASS** | `getSkillContent(name, true)` appends `references/issue-templates.md` (191 lines); verified by test `skill-dynamic-loading.test.ts:64-69` |

---

### Additional Findings

#### WARN: `skill-data/mohist/SKILL.md:61` references stale install path

Line 61 reads: `完整的模板内容也安装在 .agents/skills/mohist/issue-templates.md，可直接查看。`

This is no longer accurate after the stub-only migration. The issue templates are no longer installed to `.agents/skills/mohist/issue-templates.md` — they are served dynamically via `mo skills get mohist --full`. This line should be updated to reflect the new behavior.

**Suggestion:** Change line 61 in `skill-data/mohist/SKILL.md` to point to `mo skills get mohist --full` or `mo instructions <label>`.

---

### Verdict

**PASS with warnings.**

Two warnings noted (duplicate `issue-templates.md` content and stale reference in skill-data `SKILL.md`), neither of which breaks spec compliance. The implementation is clean, well-tested, type-safe, and faithfully follows the design decisions.

<promise>PASS</promise>
