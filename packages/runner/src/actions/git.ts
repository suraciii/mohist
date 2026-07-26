import { runCommand } from "../system/process.js"
import type { TaskLogger } from "../runtime/task-log.js"

/**
 * Single network default applied to every network-bound git/gh command
 * (clone, fetch, ls-remote, push, gh pr list/edit/create, gh --version,
 * gh auth status). The policy lives at the call site, not
 * in the primitive — the primitive (`runCommand`) provides the knob
 * (`CommandLineOptions.timeoutMs`), and each network call site opts in
 * with this constant. There is intentionally no per-command budget
 * table: a hung network command is a hung network command, regardless
 * of which subcommand it was.
 */
export const NETWORK_COMMAND_TIMEOUT_MS = 120_000

export interface GitSink {
  /**
   * Single sink for ops command output. When supplied, every line of
   * stdout / stderr emitted by the child `git` process is forwarded
   * through `log.write(source, line)` — masking, monotonic seq
   * assignment, and buffering all happen in that one chokepoint.
   * Without a sink the returned aggregate is unchanged
   * so existing callers keep working.
   */
  log: TaskLogger
  /**
   * Phase label recorded against every captured line. The host
   * provides one (e.g. `workspace-prep`, `branch-check`,
   * `action:rebase`, `cleanup`) so the line-by-line log in the web
   * viewer distinguishes ops phases by their source tag.
   */
  source: string
}

export interface GitOptions {
  sink?: GitSink | null
  /**
   * Per-command timeout in ms. Layered over the caller-supplied
   * `AbortSignal` by `runCommand`. Network-bound call
   * sites pass `NETWORK_COMMAND_TIMEOUT_MS`; local-only probes omit
   * it and run under the work-level signal only.
   */
  timeoutMs?: number
}

export async function git(workDir: string, args: string[], signal: AbortSignal, options?: GitOptions) {
  const sink = options?.sink ?? null
  const result = await runCommand("git", args, workDir, signal, undefined, sink ? {
    onLine: (line) => sink.log.write(sink.source, line),
    timeoutMs: options?.timeoutMs,
  } : { timeoutMs: options?.timeoutMs })
  return { ...result, success: result.exitCode === 0, combinedOutput: combinedOutput(result.stdout, result.stderr) }
}

export function combinedOutput(stdout: string, stderr: string) {
  return [stdout.trim(), stderr.trim()].filter(Boolean).join("\n")
}
