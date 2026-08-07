import { afterEach, beforeEach, describe, expect, it } from "vitest"
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

  describe("policy defaults", () => {
    it("null policy returns zeroed result", async () => {
      const past = new Date(fixture.now.getTime() - 10 * 24 * 60 * 60 * 1000)
      await fixture.registerEligible("wr-old", 1, past)

      const result = await fixture.loop.runOnce(null, new AbortController().signal)

      expect(result).toEqual({
        retentionRemoved: 0,
        budgetRemoved: 0,
        guardAborted: 0,
        stuckResolved: 0,
        workspaceUsageBytes: null,
      })
      expect(fixture.registry.get("wr-old")).not.toBeNull()
    })

    it("undefined policy returns zeroed result", async () => {
      const past = new Date(fixture.now.getTime() - 10 * 24 * 60 * 60 * 1000)
      await fixture.registerEligible("wr-old", 1, past)

      const result = await fixture.loop.runOnce(undefined, new AbortController().signal)

      expect(result.retentionRemoved).toBe(0)
      expect(result.budgetRemoved).toBe(0)
      expect(fixture.registry.get("wr-old")).not.toBeNull()
    })

    it("empty policy (all null) removes nothing", async () => {
      const past = new Date(fixture.now.getTime() - 10 * 24 * 60 * 60 * 1000)
      await fixture.registerEligible("wr-old", 1, past)
      fixture.runner.sizes.set(fixture.root, 2_000_000)

      const policy: CleanupPolicy = {
        retentionDays: null,
        storageBudgetBytes: null,
        storageTargetWatermarkBytes: null,
      }
      const result = await fixture.loop.runOnce(policy, new AbortController().signal)

      expect(result.retentionRemoved).toBe(0)
      expect(result.budgetRemoved).toBe(0)
    })

    it("only retention enabled evicts for age, not budget", async () => {
      const past = new Date(fixture.now.getTime() - 10 * 24 * 60 * 60 * 1000)
      await fixture.registerEligible("wr-old", 1, past)
      fixture.runner.sizes.set(fixture.root, 5_000_000)

      const policy: CleanupPolicy = { retentionDays: 7, storageBudgetBytes: null }
      const result = await fixture.loop.runOnce(policy, new AbortController().signal)

      expect(result.retentionRemoved).toBe(1)
      expect(result.budgetRemoved).toBe(0)
    })

    it("only budget enabled evicts for budget, not age", async () => {
      const recent = new Date(fixture.now.getTime() - 1 * 24 * 60 * 60 * 1000)
      await fixture.registerEligible("wr-recent", 1, recent)
      fixture.runner.sizes.set(fixture.root, 2_000_000)

      const policy: CleanupPolicy = {
        retentionDays: null,
        storageBudgetBytes: 1_000_000,
        storageTargetWatermarkBytes: 500_000,
      }
      const result = await fixture.loop.runOnce(policy, new AbortController().signal)

      expect(result.retentionRemoved).toBe(0)
      expect(result.budgetRemoved).toBe(1)
    })
  })

  describe("no eligible entries", () => {
    it("returns zeroed result when registry has no eligible entries", async () => {
      await fixture.registerActive("wr-active", 1)

      const policy: CleanupPolicy = { retentionDays: 7 }
      const result = await fixture.loop.runOnce(policy, new AbortController().signal)

      expect(result.retentionRemoved).toBe(0)
      expect(result.budgetRemoved).toBe(0)
      expect(result.guardAborted).toBe(0)
    })

    it("returns zeroed result when registry is empty", async () => {
      const policy: CleanupPolicy = { retentionDays: 7 }
      const result = await fixture.loop.runOnce(policy, new AbortController().signal)

      expect(result.retentionRemoved).toBe(0)
    })
  })

  describe("abort signal", () => {
    it("returns zeroed result when signal is already aborted", async () => {
      const past = new Date(fixture.now.getTime() - 10 * 24 * 60 * 60 * 1000)
      await fixture.registerEligible("wr-old", 1, past)

      const controller = new AbortController()
      controller.abort()
      const result = await fixture.loop.runOnce({ retentionDays: 7 }, controller.signal)

      expect(result.retentionRemoved).toBe(0)
      expect(fixture.registry.get("wr-old")).not.toBeNull()
    })
  })
})
