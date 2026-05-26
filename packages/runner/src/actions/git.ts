import { runCommand } from "../system/process.js"

export async function git(workDir: string, args: string[], signal: AbortSignal) {
  const result = await runCommand("git", args, workDir, signal)
  return { ...result, success: result.exitCode === 0, combinedOutput: combinedOutput(result.stdout, result.stderr) }
}

export function combinedOutput(stdout: string, stderr: string) {
  return [stdout.trim(), stderr.trim()].filter(Boolean).join("\n")
}
