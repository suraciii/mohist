import { runCommand } from "../system/process.js"
import type { TaskLogger } from "../runtime/task-log.js"

export interface GitSink {
  /**
   * Single sink for ops command output. When supplied, every line of
   * stdout / stderr emitted by the child `git` process is forwarded
   * through `log.write(source, line)` — masking, monotonic seq
   * assignment, and buffering all happen in that one chokepoint
   * (design D2). Without a sink the returned aggregate is unchanged
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
}

export async function git(workDir: string, args: string[], signal: AbortSignal, options?: GitOptions) {
  const sink = options?.sink ?? null
  const result = await runCommand("git", args, workDir, signal, undefined, sink ? {
    onLine: (line) => sink.log.write(sink.source, line),
  } : undefined)
  return { ...result, success: result.exitCode === 0, combinedOutput: combinedOutput(result.stdout, result.stderr) }
}

export function combinedOutput(stdout: string, stderr: string) {
  return [stdout.trim(), stderr.trim()].filter(Boolean).join("\n")
}
