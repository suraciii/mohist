import { tmpdir } from "node:os"
import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { createCleanupLoopFixture, type CleanupLoopFixture } from "./support/cleanup-loop-fixture.js"
import type { CleanupPolicy } from "../src/core/types.js"
import { capturedLogs } from "./support/logger-test.js"

describe("CleanupLoop", () => {
  let fixture: CleanupLoopFixture

  beforeEach(async () => {
    fixture = await createCleanupLoopFixture()
  })

  afterEach(async () => {
    await fixture.dispose()
  })

  describe("resolution pass", () => {
    it("resolves an out-of-root eligible entry to stuck and leaves the directory intact", async () => {
      const past = new Date(fixture.now.getTime() - 10 * 24 * 60 * 60 * 1000)
      const outPath = join(tmpdir(), "outside-workspace")
      await fixture.registerEligible("wr-out", 1, past, outPath)
      fixture.runner.outOfRootPaths.add(outPath)

      const policy: CleanupPolicy = { retentionDays: 5 }
      const result = await fixture.expectWarnings(
        [`workspace cleanup: refused to remove ${outPath} — path is outside runnerRoot`],
        () => fixture.loop.runOnce(policy, new AbortController().signal),
      )

      expect(result.stuckResolved).toBe(1)
      expect(result.guardAborted).toBe(0)
      expect(result.retentionRemoved).toBe(0)
      expect(fixture.registry.get("wr-out")).toMatchObject({ phase: "stuck" })
      expect(fixture.runner.deletedPaths).not.toContain(outPath)
    })

    it("resolves a missing-marker eligible entry to stuck and leaves the directory intact", async () => {
      const past = new Date(fixture.now.getTime() - 10 * 24 * 60 * 60 * 1000)
      const path = fixture.workspacePath(1)
      await fixture.registerEligible("wr-missing-marker", 1, past, path)
      fixture.runner.markerRunIds.delete(path)

      const policy: CleanupPolicy = { retentionDays: 5 }
      const result = await fixture.expectWarnings(
        [`workspace cleanup: refused to remove ${path} — marker is missing or unreadable`],
        () => fixture.loop.runOnce(policy, new AbortController().signal),
      )

      expect(result.stuckResolved).toBe(1)
      expect(result.guardAborted).toBe(0)
      expect(result.retentionRemoved).toBe(0)
      expect(fixture.registry.get("wr-missing-marker")).toMatchObject({ phase: "stuck" })
      expect(fixture.runner.deletedPaths).not.toContain(path)
    })

    it("resolves a marker-mismatch eligible entry to stuck and leaves the directory intact", async () => {
      const past = new Date(fixture.now.getTime() - 10 * 24 * 60 * 60 * 1000)
      const path = fixture.workspacePath(1)
      await fixture.registerEligible("wr-mismatch", 1, past, path)
      fixture.runner.markerRunIds.set(path, "wr-other-run")

      const policy: CleanupPolicy = { retentionDays: 5 }
      const result = await fixture.expectWarnings(
        [`workspace cleanup: refused to remove ${path} — marker workflowRunId (wr-other-run) does not match registry (wr-mismatch)`],
        () => fixture.loop.runOnce(policy, new AbortController().signal),
      )

      expect(result.stuckResolved).toBe(1)
      expect(result.guardAborted).toBe(0)
      expect(result.retentionRemoved).toBe(0)
      expect(fixture.registry.get("wr-mismatch")).toMatchObject({ phase: "stuck" })
      expect(fixture.runner.deletedPaths).not.toContain(path)
    })

    it("warns once for a stuck entry and does not re-warn or re-evaluate it on the next tick", async () => {
      const past = new Date(fixture.now.getTime() - 10 * 24 * 60 * 60 * 1000)
      const path = fixture.workspacePath(1)
      await fixture.registerEligible("wr-guard", 1, past, path)
      fixture.runner.outOfRootPaths.add(path)

      const policy: CleanupPolicy = { retentionDays: 5 }

      // Tick 1: guard refuses -> single warning -> entry resolved to stuck.
      const tick1 = await fixture.expectWarnings(
        [`workspace cleanup: refused to remove ${path} — path is outside runnerRoot`],
        () => fixture.loop.runOnce(policy, new AbortController().signal),
      )
      expect(tick1.stuckResolved).toBe(1)
      expect(tick1.guardAborted).toBe(0)
      expect(fixture.registry.get("wr-guard")).toMatchObject({ phase: "stuck" })

      // Tick 2: entry is stuck -> excluded from the eligible set -> no
      // per-entry work and no refusal warning.
      const warningSpy = vi.spyOn(console, "warn").mockImplementation(() => undefined)
      let tick2: { stuckResolved: number; guardAborted: number } | undefined
      try {
        tick2 = await fixture.loop.runOnce(policy, new AbortController().signal)
      } finally {
        warningSpy.mockRestore()
      }
      expect(warningSpy).not.toHaveBeenCalled()
      expect(tick2!.stuckResolved).toBe(0)
      expect(tick2!.guardAborted).toBe(0)
      expect(fixture.registry.get("wr-guard")).toMatchObject({ phase: "stuck" })
      expect(fixture.runner.deletedPaths).not.toContain(path)
    })

    it("resolves a stuck entry even when both retention and budget are disabled", async () => {
      const past = new Date(fixture.now.getTime() - 10 * 24 * 60 * 60 * 1000)
      const path = fixture.workspacePath(1)
      await fixture.registerEligible("wr-stuck", 1, past, path)
      fixture.runner.markerRunIds.delete(path)

      const policy: CleanupPolicy = {
        retentionDays: null,
        storageBudgetBytes: null,
        storageTargetWatermarkBytes: null,
      }
      const result = await fixture.expectWarnings(
        [`workspace cleanup: refused to remove ${path} — marker is missing or unreadable`],
        () => fixture.loop.runOnce(policy, new AbortController().signal),
      )

      expect(result.stuckResolved).toBe(1)
      expect(result.retentionRemoved).toBe(0)
      expect(result.budgetRemoved).toBe(0)
      expect(fixture.registry.get("wr-stuck")).toMatchObject({ phase: "stuck" })

      // Subsequent tick performs no per-entry work for the resolved entry.
      const warningSpy = vi.spyOn(console, "warn").mockImplementation(() => undefined)
      let tick2: { stuckResolved: number } | undefined
      try {
        tick2 = await fixture.loop.runOnce(policy, new AbortController().signal)
      } finally {
        warningSpy.mockRestore()
      }
      expect(warningSpy).not.toHaveBeenCalled()
      expect(tick2!.stuckResolved).toBe(0)
    })

    it("a stuck resolution survives a registry reload and does not reappear as eligible after restart", async () => {
      const past = new Date(fixture.now.getTime() - 10 * 24 * 60 * 60 * 1000)
      const path = fixture.workspacePath(1)
      await fixture.registerEligible("wr-restart", 1, past, path)
      fixture.runner.markerRunIds.delete(path)

      const policy: CleanupPolicy = { retentionDays: 5 }
      const tick1 = await fixture.expectWarnings(
        [`workspace cleanup: refused to remove ${path} — marker is missing or unreadable`],
        () => fixture.loop.runOnce(policy, new AbortController().signal),
      )
      expect(tick1.stuckResolved).toBe(1)

      // Simulate a runner restart: the persisted `stuck` phase is reloaded.
      await fixture.registry.reload()
      expect(fixture.registry.get("wr-restart")).toMatchObject({ phase: "stuck" })

      // Post-restart tick: the stuck entry is not eligible, so it is
      // neither re-evaluated nor re-warned.
      const warningSpy = vi.spyOn(console, "warn").mockImplementation(() => undefined)
      let tick2: { stuckResolved: number } | undefined
      try {
        tick2 = await fixture.loop.runOnce(policy, new AbortController().signal)
      } finally {
        warningSpy.mockRestore()
      }
      expect(warningSpy).not.toHaveBeenCalled()
      expect(tick2!.stuckResolved).toBe(0)
      expect(fixture.registry.get("wr-restart")).toMatchObject({ phase: "stuck" })
    })

    it("still removes an eligible entry whose guards pass (resolution does not weaken normal eviction)", async () => {
      const past = new Date(fixture.now.getTime() - 10 * 24 * 60 * 60 * 1000)
      const path = fixture.workspacePath(1)
      await fixture.registerEligible("wr-clean", 1, past, path)

      const policy: CleanupPolicy = { retentionDays: 5 }
      const warningSpy = vi.spyOn(console, "warn").mockImplementation(() => undefined)
      let result
      try {
        result = await fixture.loop.runOnce(policy, new AbortController().signal)
      } finally {
        warningSpy.mockRestore()
      }
      expect(warningSpy).not.toHaveBeenCalled()
      expect(result!.stuckResolved).toBe(0)
      expect(result!.retentionRemoved).toBe(1)
      expect(fixture.registry.get("wr-clean")).toBeNull()
      expect(fixture.runner.deletedPaths).toContain(path)
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

      const removed = await fixture.loop.safeRemove(entry)

      expect(removed).toBe(false)
      expect(fixture.registry.get("wr-1")).not.toBeNull()
      expect(capturedLogs()).toEqual(expect.arrayContaining([
        expect.objectContaining({ level: "ERROR", message: "workspace cleanup failed to remove path", fields: expect.objectContaining({ path, exception: expect.objectContaining({ name: "Error", message: `stub delete failed: ${path}` }) }) }),
      ]))
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
