import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { createCleanupLoopFixture, type CleanupLoopFixture } from "./support/cleanup-loop-fixture.js"
import type { CleanupPolicy } from "../src/core/types.js"

describe("CleanupLoop", () => {
  let fixture: CleanupLoopFixture

  beforeEach(async () => {
    fixture = await createCleanupLoopFixture()
  })

  afterEach(async () => {
    await fixture.dispose()
  })

  describe("pre-delete guards", () => {
    it("aborts removal when workspace path is outside runnerRoot", async () => {
      const past = new Date(fixture.now.getTime() - 10 * 24 * 60 * 60 * 1000)
      const outPath = join(tmpdir(), "outside-workspace")
      await fixture.registerEligible("wr-out", 1, past, outPath)
      fixture.runner.outOfRootPaths.add(outPath)

      const policy: CleanupPolicy = { retentionDays: 5 }
      const result = await fixture.expectWarnings(
        [`workspace cleanup: refused to remove ${outPath} — path is outside runnerRoot`],
        () => fixture.loop.runOnce(policy, new AbortController().signal),
      )

      expect(result.guardAborted).toBe(1)
      expect(result.retentionRemoved).toBe(0)
      expect(fixture.registry.get("wr-out")).not.toBeNull()
      expect(fixture.runner.deletedPaths).not.toContain(outPath)
    })

    it("aborts removal when marker is missing", async () => {
      const past = new Date(fixture.now.getTime() - 10 * 24 * 60 * 60 * 1000)
      const path = fixture.workspacePath(1)
      await fixture.registerEligible("wr-missing-marker", 1, past, path)
      fixture.runner.markerRunIds.delete(path)

      const policy: CleanupPolicy = { retentionDays: 5 }
      const result = await fixture.expectWarnings(
        [`workspace cleanup: refused to remove ${path} — marker is missing or unreadable`],
        () => fixture.loop.runOnce(policy, new AbortController().signal),
      )

      expect(result.guardAborted).toBe(1)
      expect(result.retentionRemoved).toBe(0)
      expect(fixture.registry.get("wr-missing-marker")).not.toBeNull()
    })

    it("aborts removal when marker workflowRunId mismatches registry", async () => {
      const past = new Date(fixture.now.getTime() - 10 * 24 * 60 * 60 * 1000)
      const path = fixture.workspacePath(1)
      await fixture.registerEligible("wr-mismatch", 1, past, path)
      fixture.runner.markerRunIds.set(path, "wr-other-run")

      const policy: CleanupPolicy = { retentionDays: 5 }
      const result = await fixture.expectWarnings(
        [`workspace cleanup: refused to remove ${path} — marker workflowRunId (wr-other-run) does not match registry (wr-mismatch)`],
        () => fixture.loop.runOnce(policy, new AbortController().signal),
      )

      expect(result.guardAborted).toBe(1)
      expect(result.retentionRemoved).toBe(0)
      expect(fixture.registry.get("wr-mismatch")).not.toBeNull()
    })

    it("after guard abort, directory and entry remain intact for next tick", async () => {
      const past = new Date(fixture.now.getTime() - 10 * 24 * 60 * 60 * 1000)
      const path = fixture.workspacePath(1)
      await fixture.registerEligible("wr-guard", 1, past, path)
      fixture.runner.outOfRootPaths.add(path)

      const policy: CleanupPolicy = { retentionDays: 5 }
      let result = await fixture.expectWarnings(
        [`workspace cleanup: refused to remove ${path} — path is outside runnerRoot`],
        () => fixture.loop.runOnce(policy, new AbortController().signal),
      )
      expect(result.guardAborted).toBe(1)
      expect(fixture.registry.get("wr-guard")).not.toBeNull()

      result = await fixture.expectWarnings(
        [`workspace cleanup: refused to remove ${path} — path is outside runnerRoot`],
        () => fixture.loop.runOnce(policy, new AbortController().signal),
      )
      expect(result.guardAborted).toBe(1)
      expect(fixture.registry.get("wr-guard")).not.toBeNull()
    })
  })

  describe("safeRemove", () => {
    it("successful removal deletes directory and registry entry", async () => {
      const path = fixture.workspacePath(1)
      const entry = await fixture.registerActive("wr-1", 1, path)

      const removed = await fixture.loop.safeRemove(entry)

      expect(removed).toBe(true)
      expect(fixture.registry.get("wr-1")).toBeNull()
      expect(fixture.runner.deletedPaths).toContain(path)
    })

    it("failed deleteDirectory does not remove registry entry", async () => {
      const path = fixture.workspacePath(1)
      const entry = await fixture.registerActive("wr-1", 1, path)
      fixture.runner.failedDeletePaths.add(path)

      const errorSpy = vi.spyOn(console, "error").mockImplementation(() => undefined)
      try {
        const removed = await fixture.loop.safeRemove(entry)

        expect(removed).toBe(false)
        expect(fixture.registry.get("wr-1")).not.toBeNull()
        expect(errorSpy).toHaveBeenCalledTimes(1)
        expect(errorSpy).toHaveBeenNthCalledWith(
          1,
          `workspace cleanup: failed to remove ${path}:`,
          expect.objectContaining({ name: "Error", message: `stub delete failed: ${path}` }),
        )
      } finally {
        errorSpy.mockRestore()
      }
    })

    it("out-of-root path aborts before any deletion", async () => {
      const outPath = join(tmpdir(), "outside")
      const entry = await fixture.registerActive("wr-outside", 1, outPath)
      fixture.runner.outOfRootPaths.add(outPath)
      fixture.runner.markerRunIds.set(outPath, "wr-outside")

      const removed = await fixture.expectWarnings(
        [`workspace cleanup: refused to remove ${outPath} — path is outside runnerRoot`],
        () => fixture.loop.safeRemove(entry),
      )

      expect(removed).toBe(false)
      expect(fixture.registry.get("wr-outside")).not.toBeNull()
      expect(fixture.runner.deletedPaths).not.toContain(outPath)
    })

    it("marker mismatch aborts before deletion", async () => {
      const path = fixture.workspacePath(1)
      const entry = await fixture.registerActive("wr-1", 1, path)
      fixture.runner.markerRunIds.set(path, "wr-other")

      const removed = await fixture.expectWarnings(
        [`workspace cleanup: refused to remove ${path} — marker workflowRunId (wr-other) does not match registry (wr-1)`],
        () => fixture.loop.safeRemove(entry),
      )

      expect(removed).toBe(false)
      expect(fixture.registry.get("wr-1")).not.toBeNull()
      expect(fixture.runner.deletedPaths).not.toContain(path)
    })

    it("missing marker aborts before deletion", async () => {
      const path = fixture.workspacePath(1)
      const entry = await fixture.registerActive("wr-1", 1, path)
      fixture.runner.markerRunIds.delete(path)

      const removed = await fixture.expectWarnings(
        [`workspace cleanup: refused to remove ${path} — marker is missing or unreadable`],
        () => fixture.loop.safeRemove(entry),
      )

      expect(removed).toBe(false)
      expect(fixture.registry.get("wr-1")).not.toBeNull()
      expect(fixture.runner.deletedPaths).not.toContain(path)
    })
  })
})
