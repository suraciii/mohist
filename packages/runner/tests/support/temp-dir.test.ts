import { expect, it } from "vitest"
import { cleanupRegisteredTempDirs, createTestTempDir, createTestTempDirSync } from "./temp-dir.js"
import { MemoryFileSystem } from "./memory-filesystem.js"
import { withTestRunnerResources } from "./test-resources.js"

it("cleans registered asynchronous and synchronous directories", async () => {
  const fileSystem = new MemoryFileSystem()
  await withTestRunnerResources(async () => {
    const asynchronous = await createTestTempDir("mohist-temp-async-")
    const synchronous = createTestTempDirSync("mohist-temp-sync-")

    expect(fileSystem.exists(asynchronous)).toBe(true)
    expect(fileSystem.exists(synchronous)).toBe(true)

    await cleanupRegisteredTempDirs()

    expect(fileSystem.exists(asynchronous)).toBe(false)
    expect(fileSystem.exists(synchronous)).toBe(false)
  }, { fileSystem })
})

it("ignores a registered directory removed by the test", async () => {
  const fileSystem = new MemoryFileSystem()
  await withTestRunnerResources(async () => {
    const path = await createTestTempDir("mohist-temp-absent-")
    await fileSystem.deleteDirectory(path)

    await expect(cleanupRegisteredTempDirs()).resolves.toBeUndefined()
  }, { fileSystem })
})
