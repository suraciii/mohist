import type { JsonObject, RenderedWorkItem } from "../core/types.js"
import { stringInput } from "../core/json.js"

export const AGENT_BACKED_USES = "mohist/opencode"
export const DEFAULT_MAX_CLEANUP_ATTEMPTS = 3

export interface WorktreeSnapshot {
  staged: string[]
  unstaged: string[]
  untracked: string[]
  isClean: boolean
}

export function isAgentBackedTask(work: Pick<RenderedWorkItem, "uses">): boolean {
  if (typeof work.uses !== "string") return false
  const normalized = work.uses.trim().toLowerCase()
  return normalized === AGENT_BACKED_USES
}

export function resolveMaxCleanupAttempts(variables: JsonObject): number {
  const candidate = variables["runner"]
  if (candidate && typeof candidate === "object" && !Array.isArray(candidate)) {
    const cleanup = (candidate as JsonObject)["cleanup"]
    if (cleanup && typeof cleanup === "object" && !Array.isArray(cleanup)) {
      const value = (cleanup as JsonObject)["maxAttempts"]
      if (typeof value === "number" && Number.isFinite(value) && value >= 0) return Math.floor(value)
      if (typeof value === "string") {
        const parsed = Number(value)
        if (Number.isFinite(parsed) && parsed >= 0) return Math.floor(parsed)
      }
    }
  }
  return DEFAULT_MAX_CLEANUP_ATTEMPTS
}

export function buildCleanupWith(work: RenderedWorkItem, renderedWith: JsonObject | null, snapshot: WorktreeSnapshot, attempt: number): JsonObject {
  const existingWith = renderedWith ?? {}
  const existingSession = stringInput(existingWith as JsonObject, "session")
  const basePrompt = stringInput(existingWith as JsonObject, "prompt")
  const originalTitle = work.title?.trim() || work.uses || work.workId
  const cleanupWith: JsonObject = { ...existingWith }
  cleanupWith["prompt"] = buildCleanupPrompt({
    basePrompt,
    title: originalTitle,
    workId: work.workId,
    attempt,
    snapshot,
  })
  if (existingSession) cleanupWith["session"] = existingSession
  return cleanupWith
}

export function buildCleanupPrompt(input: {
  basePrompt: string | undefined
  title: string
  workId: string
  attempt: number
  snapshot: WorktreeSnapshot
}): string {
  const staged = input.snapshot.staged
  const unstaged = input.snapshot.unstaged
  const untracked = input.snapshot.untracked
  const sections: string[] = []

  sections.push(`## Cleanup Follow-up (attempt ${input.attempt}) for ${input.title} (${input.workId})`)
  sections.push("")
  sections.push("The previous run of this task reported success but left uncommitted changes in the worktree. The task cannot be marked completed until the worktree is clean.")
  sections.push("")
  sections.push("### Hard constraints")
  sections.push("- Do NOT start any new task work. The original task is already considered done by the runner.")
  sections.push("- Do NOT push to any remote. Do not run `git push`, do not open a pull request, do not update a remote branch.")
  sections.push("- Do NOT modify files outside the scope of cleaning up the worktree. The only allowed operations are: `git add`, `git commit`, `git checkout -- <file>`, `git restore <file>`, and `git clean` (with care).")
  sections.push("- Do NOT close or replace the current agent session. The runner will continue this same session.")
  sections.push("")
  sections.push("### Current worktree state")
  sections.push(formatFileSection("Staged (added to index)", staged))
  sections.push(formatFileSection("Unstaged (modified in working tree)", unstaged))
  sections.push(formatFileSection("Untracked (not in index or working tree)", untracked))
  sections.push("")
  sections.push("### What to do")
  sections.push("1. For every file above, decide whether it is part of the original task output that should be kept, or unrelated noise that should be reverted.")
  sections.push("2. Commit task-related changes (keep) with `git add <file-or-dir> && git commit -m \"<short message>\"`. Use a clear message that names the task. Commit task-related changes or revert unrelated ones — the runner needs the worktree to be clean before the task can complete.")
  sections.push("3. Revert unrelated changes (discard) with `git checkout -- <file>` or `git restore <file>`. Remove untracked noise with `git clean -fd <path>` only when you are sure it is safe.")
  sections.push("4. End the run with `git status --porcelain` showing no output. The runner will re-check cleanliness after you return.")
  sections.push("5. In your final summary, report either:")
  sections.push("   - the commit SHA(s) you created (e.g. `Committed abc1234` or `Committed abc1234, def5678`)")
  sections.push("   - or `no-change` if you determined the worktree was already clean and made no commit.")
  sections.push("")
  if (input.basePrompt?.trim()) {
    sections.push("### Original task prompt (for context only — do not re-execute)")
    sections.push("> The original task asked for: " + input.basePrompt.trim().split("\n")[0])
    sections.push("")
  }
  sections.push(`Cleanup attempt counter: ${input.attempt}. The runner will retry up to its configured bound and then fail the task with structured dirty-worktree evidence.`)
  return sections.join("\n")
}

function formatFileSection(label: string, files: string[]): string {
  if (files.length === 0) return `- ${label}: (none)`
  return [`- ${label}:`, ...files.map((file) => `  - ${file}`)].join("\n")
}
