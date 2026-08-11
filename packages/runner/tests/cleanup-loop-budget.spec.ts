import { describe, expect, it as vitestIt } from "vitest"
import { scopedCleanupLoopFixture, withCleanupLoopFixture } from "./support/cleanup-loop-fixture.js"
import type { CleanupPolicy } from "../src/core/types.js"

describe("CleanupLoop", () => {
  const fixture = scopedCleanupLoopFixture()

  function it(name: string, body: () => Promise<void>): void {
    vitestIt(name, () => withCleanupLoopFixture(body))
  }

  describe("budget eviction", () => {
    it("evicts earliest-terminalAt-first when usage exceeds budget", async () => {
      const t1 = new Date(fixture.now.getTime() - 8 * 24 * 60 * 60 * 1000)
      const t2 = new Date(fixture.now.getTime() - 5 * 24 * 60 * 60 * 1000)
      const t3 = new Date(fixture.now.getTime() - 3 * 24 * 60 * 60 * 1000)
      const p1 = await fixture.registerEligible("wr-1", 1, t1)
      const p2 = await fixture.registerEligible("wr-2", 2, t2)
      const p3 = await fixture.registerEligible("wr-3", 3, t3)

      fixture.runner.sizes.set(fixture.root, 1_500_000)
      fixture.runner.sizes.set(p1, 500_000)
      fixture.runner.sizes.set(p2, 400_000)
      fixture.runner.sizes.set(p3, 300_000)

      const policy: CleanupPolicy = {
        storageBudgetBytes: 500_000,
        storageTargetWatermarkBytes: 200_000,
      }
      const result = await fixture.loop.runOnce(policy, new AbortController().signal)

      expect(result.budgetRemoved).toBe(3)
      expect(fixture.registry.get("wr-1")).toBeNull()
      expect(fixture.registry.get("wr-2")).toBeNull()
      expect(fixture.registry.get("wr-3")).toBeNull()
    })

    it("stops evicting when usage drops below target watermark", async () => {
      const t1 = new Date(fixture.now.getTime() - 8 * 24 * 60 * 60 * 1000)
      const t2 = new Date(fixture.now.getTime() - 5 * 24 * 60 * 60 * 1000)
      const p1 = await fixture.registerEligible("wr-1", 1, t1)
      const p2 = await fixture.registerEligible("wr-2", 2, t2)

      fixture.runner.sizes.set(fixture.root, 1_000_000)
      fixture.runner.sizes.set(p1, 600_000)
      fixture.runner.sizes.set(p2, 300_000)

      const policy: CleanupPolicy = {
        storageBudgetBytes: 800_000,
        storageTargetWatermarkBytes: 500_000,
      }
      const result = await fixture.loop.runOnce(policy, new AbortController().signal)

      expect(result.budgetRemoved).toBe(1)
      expect(fixture.registry.get("wr-1")).toBeNull()
      expect(fixture.registry.get("wr-2")).not.toBeNull()
    })

    it("budget disabled (null) disables budget-based eviction", async () => {
      const t1 = new Date(fixture.now.getTime() - 8 * 24 * 60 * 60 * 1000)
      await fixture.registerEligible("wr-1", 1, t1)
      fixture.runner.sizes.set(fixture.root, 2_000_000)

      const policy: CleanupPolicy = { storageBudgetBytes: null }
      const result = await fixture.loop.runOnce(policy, new AbortController().signal)

      expect(result.budgetRemoved).toBe(0)
      expect(fixture.registry.get("wr-1")).not.toBeNull()
    })

    it("budget disabled (0) disables budget-based eviction", async () => {
      const t1 = new Date(fixture.now.getTime() - 8 * 24 * 60 * 60 * 1000)
      await fixture.registerEligible("wr-1", 1, t1)
      fixture.runner.sizes.set(fixture.root, 2_000_000)

      const policy: CleanupPolicy = { storageBudgetBytes: 0 }
      const result = await fixture.loop.runOnce(policy, new AbortController().signal)

      expect(result.budgetRemoved).toBe(0)
    })

    it("never evicts active entries during budget eviction", async () => {
      const t1 = new Date(fixture.now.getTime() - 8 * 24 * 60 * 60 * 1000)
      await fixture.registerActive("wr-active", 1)
      await fixture.registerEligible("wr-eligible", 2, t1)
      fixture.runner.sizes.set(fixture.root, 2_000_000)

      const policy: CleanupPolicy = {
        storageBudgetBytes: 1_000_000,
        storageTargetWatermarkBytes: 500_000,
      }
      const result = await fixture.loop.runOnce(policy, new AbortController().signal)

      expect(result.budgetRemoved).toBe(1)
      expect(fixture.registry.get("wr-active")).not.toBeNull()
      expect(fixture.registry.get("wr-eligible")).toBeNull()
    })
  })

  describe("active-entry protection", () => {
    it("never removes active entries by budget", async () => {
      await fixture.registerActive("wr-active", 1)
      const t1 = new Date(fixture.now.getTime() - 8 * 24 * 60 * 60 * 1000)
      await fixture.registerEligible("wr-old", 2, t1)
      fixture.runner.sizes.set(fixture.root, 2_000_000)

      const policy: CleanupPolicy = {
        storageBudgetBytes: 1_000_000,
        storageTargetWatermarkBytes: 500_000,
      }
      const result = await fixture.loop.runOnce(policy, new AbortController().signal)

      expect(result.budgetRemoved).toBe(1)
      expect(fixture.registry.get("wr-active")).not.toBeNull()
      expect(fixture.registry.get("wr-old")).toBeNull()
    })
  })

  describe("budget eviction ordering", () => {
    it("evicts in ascending terminalAt order", async () => {
      const t1 = new Date("2026-06-01T00:00:00Z")
      const t2 = new Date("2026-06-10T00:00:00Z")
      const t3 = new Date("2026-06-20T00:00:00Z")
      const p1 = await fixture.registerEligible("wr-first", 1, t1)
      const p2 = await fixture.registerEligible("wr-second", 2, t2)
      const p3 = await fixture.registerEligible("wr-third", 3, t3)

      fixture.runner.sizes.set(fixture.root, 2_000_000)
      fixture.runner.sizes.set(p1, 500_000)
      fixture.runner.sizes.set(p2, 500_000)
      fixture.runner.sizes.set(p3, 500_000)

      const policy: CleanupPolicy = {
        storageBudgetBytes: 500_000,
        storageTargetWatermarkBytes: 500_000,
      }
      const result = await fixture.loop.runOnce(policy, new AbortController().signal)

      expect(result.budgetRemoved).toBe(3)
      expect(fixture.runner.deletedPaths).toEqual([p1, p2, p3])
    })

    it("stops mid-way when watermark is reached", async () => {
      const t1 = new Date("2026-06-01T00:00:00Z")
      const t2 = new Date("2026-06-10T00:00:00Z")
      const p1 = await fixture.registerEligible("wr-first", 1, t1)
      const p2 = await fixture.registerEligible("wr-second", 2, t2)

      fixture.runner.sizes.set(fixture.root, 2_000_000)
      fixture.runner.sizes.set(p1, 600_000)
      fixture.runner.sizes.set(p2, 300_000)

      const policy: CleanupPolicy = {
        storageBudgetBytes: 1_500_000,
        storageTargetWatermarkBytes: 1_200_000,
      }
      const result = await fixture.loop.runOnce(policy, new AbortController().signal)

      expect(result.budgetRemoved).toBe(2)
      expect(fixture.registry.get("wr-first")).toBeNull()
      expect(fixture.registry.get("wr-second")).toBeNull()
    })

    it("when usage is already under budget, no eviction occurs", async () => {
      const t1 = new Date("2026-06-01T00:00:00Z")
      await fixture.registerEligible("wr-1", 1, t1)
      fixture.runner.sizes.set(fixture.root, 100_000)

      const policy: CleanupPolicy = {
        storageBudgetBytes: 1_000_000,
        storageTargetWatermarkBytes: 500_000,
      }
      const result = await fixture.loop.runOnce(policy, new AbortController().signal)

      expect(result.budgetRemoved).toBe(0)
      expect(fixture.registry.get("wr-1")).not.toBeNull()
    })
  })
})
