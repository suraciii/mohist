import { describe, expect, it as vitestIt } from "vitest"
import { scopedCleanupLoopFixture, withCleanupLoopFixture } from "./support/cleanup-loop-fixture.js"
import type { CleanupPolicy } from "../src/core/types.js"

describe("CleanupLoop", () => {
  const fixture = scopedCleanupLoopFixture()

  function it(name: string, body: () => Promise<void>): void {
    vitestIt(name, () => withCleanupLoopFixture(body))
  }

  describe("retention eviction", () => {
    it("removes eligible workspace past the retention window", async () => {
      const past = new Date(fixture.now.getTime() - 10 * 24 * 60 * 60 * 1000)
      await fixture.registerEligible("wr-old", 1, past)

      const policy: CleanupPolicy = { retentionDays: 7 }
      const result = await fixture.loop.runOnce(policy, new AbortController().signal)

      expect(result.retentionRemoved).toBe(1)
      expect(result.budgetRemoved).toBe(0)
      expect(fixture.registry.get("wr-old")).toBeNull()
    })

    it("keeps eligible workspace within the retention window", async () => {
      const recent = new Date(fixture.now.getTime() - 3 * 24 * 60 * 60 * 1000)
      await fixture.registerEligible("wr-recent", 1, recent)

      const policy: CleanupPolicy = { retentionDays: 7 }
      const result = await fixture.loop.runOnce(policy, new AbortController().signal)

      expect(result.retentionRemoved).toBe(0)
      expect(fixture.registry.get("wr-recent")).not.toBeNull()
    })

    it("retention disabled (null) disables age-based eviction", async () => {
      const past = new Date(fixture.now.getTime() - 10 * 24 * 60 * 60 * 1000)
      await fixture.registerEligible("wr-old", 1, past)

      const policy: CleanupPolicy = { retentionDays: null }
      const result = await fixture.loop.runOnce(policy, new AbortController().signal)

      expect(result.retentionRemoved).toBe(0)
      expect(fixture.registry.get("wr-old")).not.toBeNull()
    })

    it("retention disabled (0) disables age-based eviction", async () => {
      const past = new Date(fixture.now.getTime() - 10 * 24 * 60 * 60 * 1000)
      await fixture.registerEligible("wr-old", 1, past)

      const policy: CleanupPolicy = { retentionDays: 0 }
      const result = await fixture.loop.runOnce(policy, new AbortController().signal)

      expect(result.retentionRemoved).toBe(0)
    })

    it("removes only eligible entries past the window, keeping recent ones", async () => {
      const oldAt = new Date(fixture.now.getTime() - 10 * 24 * 60 * 60 * 1000)
      const recentAt = new Date(fixture.now.getTime() - 2 * 24 * 60 * 60 * 1000)
      await fixture.registerEligible("wr-old", 1, oldAt)
      await fixture.registerEligible("wr-recent", 2, recentAt)

      const policy: CleanupPolicy = { retentionDays: 5 }
      const result = await fixture.loop.runOnce(policy, new AbortController().signal)

      expect(result.retentionRemoved).toBe(1)
      expect(fixture.registry.get("wr-old")).toBeNull()
      expect(fixture.registry.get("wr-recent")).not.toBeNull()
    })

    it("does not evict eligible entries without terminalAt (no age measurement)", async () => {
      const path = await fixture.registerEligibleWithoutTerminalAt("wr-noterminal", 99)

      const result = await fixture.loop.runOnce({ retentionDays: 7 }, new AbortController().signal)

      expect(result).toEqual({
        retentionRemoved: 0,
        budgetRemoved: 0,
        guardAborted: 0,
        stuckResolved: 0,
        workspaceUsageBytes: null,
      })
      expect(fixture.registry.get("wr-noterminal")).toMatchObject({ phase: "eligible", terminalAt: null })
      expect(fixture.runner.deletedPaths).not.toContain(path)
    })
  })

  describe("active-entry protection", () => {
    it("never removes active entries by retention", async () => {
      await fixture.registerActive("wr-active", 1)
      const past = new Date(fixture.now.getTime() - 10 * 24 * 60 * 60 * 1000)
      await fixture.registerEligible("wr-old", 2, past)

      const policy: CleanupPolicy = { retentionDays: 5 }
      const result = await fixture.loop.runOnce(policy, new AbortController().signal)

      expect(result.retentionRemoved).toBe(1)
      expect(fixture.registry.get("wr-active")).not.toBeNull()
      expect(fixture.registry.get("wr-old")).toBeNull()
    })
  })
})
