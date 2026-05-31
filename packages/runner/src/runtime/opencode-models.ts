import { execFile } from "node:child_process"

export async function discoverOpencodeModels(signal: AbortSignal): Promise<string[]> {
  const command = process.env.MOHIST_AGENT_MODELS_COMMAND ?? process.env.MOHIST_AGENT_COMMAND ?? "opencode"

  try {
    const stdout = await execFileText(command, ["models"], signal)
    return stdout
      .split(/\r?\n/)
      .map((line) => line.trim())
      .filter((line) => line.length > 0)
  } catch (error) {
    console.error("failed to discover opencode models", error)
    return []
  }
}

function execFileText(command: string, args: string[], signal: AbortSignal): Promise<string> {
  return new Promise((resolve, reject) => {
    const child = execFile(command, args, { signal, timeout: 10_000 }, (error, stdout) => {
      if (error) reject(error)
      else resolve(stdout)
    })
    child.unref?.()
  })
}
