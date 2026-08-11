import { join } from "node:path"
import { currentRunnerFileSystem, type RunnerFileSystem } from "../../src/system/filesystem.js"
import { cleanupTestTempDirs, currentTestResourceState, registerTestTempDir } from "./test-resources.js"

export async function createTestTempDir(prefix: string): Promise<string> {
  const state = currentTestResourceState()
  const path = join("/virtual", `${prefix}${state.nextTempId++}`)
  const fileSystem = currentRunnerFileSystem()
  await fileSystem.ensureDir(path)
  registerTestTempDir(path)
  return path
}

export function createTestTempDirSync(prefix: string): string {
  const state = currentTestResourceState()
  const path = join("/virtual", `${prefix}${state.nextTempId++}`)
  const fileSystem = currentRunnerFileSystem()
  const syncFileSystem = fileSystem as RunnerFileSystem & { ensureDirSync?: (path: string) => void }
  syncFileSystem.ensureDirSync?.(path)
  registerTestTempDir(path)
  return path
}

export const cleanupRegisteredTempDirs = cleanupTestTempDirs
