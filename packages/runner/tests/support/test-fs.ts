import { currentRunnerFileSystem } from "../../src/system/filesystem.js"

export async function mkdir(path: string, _options?: { recursive?: boolean }): Promise<void> {
  await currentRunnerFileSystem().ensureDir(path)
}

export async function writeFile(path: string, content: string | Uint8Array, _options?: unknown): Promise<void> {
  if (typeof content === "string") {
    await currentRunnerFileSystem().writeText(path, content)
    return
  }
  await currentRunnerFileSystem().writeBinary(path, content)
}

export async function readFile(path: string, encoding?: "utf8" | { encoding?: "utf8" } | null): Promise<string | Uint8Array> {
  if (encoding === "utf8" || (encoding && encoding.encoding === "utf8")) return await currentRunnerFileSystem().readText(path)
  return await currentRunnerFileSystem().readBinary(path)
}

export async function rm(path: string, options?: { recursive?: boolean; force?: boolean }): Promise<void> {
  if (options?.recursive) {
    await currentRunnerFileSystem().deleteDirectory(path)
    return
  }
  await currentRunnerFileSystem().deleteFile(path)
}

export async function stat(path: string) {
  return await currentRunnerFileSystem().stat(path)
}

export async function lstat(path: string) {
  return await currentRunnerFileSystem().lstat(path)
}

export async function readdir(path: string) {
  return await currentRunnerFileSystem().readdir(path)
}

export async function symlink(target: string, path: string): Promise<void> {
  await currentRunnerFileSystem().symlink(target, path)
}

export function existsSync(path: string): boolean {
  return currentRunnerFileSystem().exists(path)
}
