import { mkdtemp, rm } from "node:fs/promises"
import { mkdtempSync } from "node:fs"
import { tmpdir } from "node:os"
import { join } from "node:path"

const registeredTempDirs: string[] = []

export async function createTestTempDir(prefix: string): Promise<string> {
  const path = await mkdtemp(join(tmpdir(), prefix))
  registeredTempDirs.push(path)
  return path
}

export function createTestTempDirSync(prefix: string): string {
  const path = mkdtempSync(join(tmpdir(), prefix))
  registeredTempDirs.push(path)
  return path
}

export async function cleanupRegisteredTempDirs(): Promise<void> {
  const errors: unknown[] = []

  while (registeredTempDirs.length > 0) {
    const path = registeredTempDirs.pop()
    if (path === undefined) continue

    try {
      await rm(path, { recursive: true })
    } catch (error) {
      if (isAbsentPathError(error)) continue
      errors.push(error)
    }
  }

  if (errors.length === 1) throw errors[0]
  if (errors.length > 1) throw new AggregateError(errors, "Failed to clean test temp directories")
}

function isAbsentPathError(error: unknown): boolean {
  return typeof error === "object" && error !== null && "code" in error && error.code === "ENOENT"
}
