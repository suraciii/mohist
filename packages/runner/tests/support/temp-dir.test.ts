import { existsSync, rmSync } from "node:fs"
import { expect, it } from "vitest"
import { cleanupRegisteredTempDirs, createTestTempDir, createTestTempDirSync } from "./temp-dir.js"

it("cleans registered asynchronous and synchronous directories", async () => {
  const asynchronous = await createTestTempDir("mohist-temp-async-")
  const synchronous = createTestTempDirSync("mohist-temp-sync-")

  expect(existsSync(asynchronous)).toBe(true)
  expect(existsSync(synchronous)).toBe(true)

  await cleanupRegisteredTempDirs()

  expect(existsSync(asynchronous)).toBe(false)
  expect(existsSync(synchronous)).toBe(false)
})

it("ignores a registered directory removed by the test", async () => {
  const path = await createTestTempDir("mohist-temp-absent-")
  rmSync(path, { recursive: true })

  await expect(cleanupRegisteredTempDirs()).resolves.toBeUndefined()
})
