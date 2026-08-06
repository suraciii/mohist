import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it } from "vitest"
import { AgentWorkspaceRegistry } from "../src/runtime/agent-workspace-registry.js"
import { AgentWorkspaceIdleProbe, type AgentWorkspaceActivityConnection } from "../src/runtime/agent-workspace-idle.js"
import type { AgentWorkspaceActivity } from "../src/server/connection.js"
import type { CleanupPolicy } from "../src/core/types.js"
import { createTestTempDir } from "./support/temp-dir.js"
import { installLoggerCapture } from "./support/logger-test.js"
import { validChildSessionId } from "./support/agent-workspace-fixture.js"

const NOW = new Date("2026-01-10T00:00:00.000Z")
// retentionDays = 7 → cutoff = 2026-01-03T00:00:00.000Z.
const RETENTION_7: CleanupPolicy = { retentionDays: 7 }
const CUTOFF_ISO = "2026-01-03T00:00:00.000Z"

type Response = AgentWorkspaceActivity | Error

interface Fixture {
  root: string
  registry: AgentWorkspaceRegistry
  probe(responses: Map<string, Response>): { probe: AgentWorkspaceIdleProbe; connection: AgentWorkspaceActivityConnection & { calls: string[] } }
}

async function fixture(childIds: Array<{ id: string; projectId?: string | null }>): Promise<Fixture> {
  const root = await createTestTempDir("mohist-idle-probe-")
  const registry = new AgentWorkspaceRegistry(root, { now: () => NOW })
  await registry.load()
  for (const { id, projectId = "project-1" } of childIds) {
    await registry.register({
      childSessionId: id,
      projectId,
      workspaceIdentity: `agent-wt:${id}`,
      workspacePath: join(root, "agent-workspaces", id),
      branch: `mohist/wt-${id}`,
      parentWorkDir: join(root, "workspaces", "wr-1"),
      repositoryName: "main",
    })
  }
  const probe = (responses: Map<string, Response>) => {
    const connection = fakeConnection(responses)
    return { probe: new AgentWorkspaceIdleProbe({ registry, connection, now: () => NOW }), connection }
  }
  return { root, registry, probe }
}

function fakeConnection(responses: Map<string, Response>): AgentWorkspaceActivityConnection & { calls: string[] } {
  const calls: string[] = []
  return {
    calls,
    getAgentWorkspaceActivity: async (projectId, childSessionId) => {
      calls.push(`${projectId}/${childSessionId}`)
      const value = responses.get(childSessionId)
      if (value instanceof Error) throw value
      return value ?? { state: "unknown", idleSince: null }
    },
  }
}

describe("AgentWorkspaceIdleProbe eligibility", () => {
  let restoreLogger: () => void

  beforeEach(() => {
    restoreLogger = installLoggerCapture()
  })

  afterEach(() => {
    restoreLogger()
  })

  it("marks eligible only for idle sessions idle longer than the retention window", async () => {
    const past = validChildSessionId(1)
    const fresh = validChildSessionId(2)
    const { registry, probe } = await fixture([{ id: past }, { id: fresh }])
    const { probe: idleProbe } = probe(new Map<string, Response>([
      [past, { state: "idle", idleSince: "2026-01-02T00:00:00.000Z" }], // before cutoff → eligible
      [fresh, { state: "idle", idleSince: "2026-01-05T00:00:00.000Z" }], // after cutoff → active
    ]))

    const result = await idleProbe.runOnce(RETENTION_7, new AbortController().signal)

    expect(result.markedEligible).toBe(1)
    expect(registry.get(past)?.phase).toBe("eligible")
    expect(registry.get(fresh)?.phase).toBe("active")
  })

  it("treats an idleSince exactly at the cutoff as not yet eligible", async () => {
    const id = validChildSessionId(1)
    const { registry, probe } = await fixture([{ id }])
    const { probe: idleProbe } = probe(new Map([[id, { state: "idle", idleSince: CUTOFF_ISO }]]))

    const result = await idleProbe.runOnce(RETENTION_7, new AbortController().signal)

    expect(result.markedEligible).toBe(0)
    expect(registry.get(id)?.phase).toBe("active")
  })

  it.each([
    ["active", { state: "active", idleSince: null }],
    ["pending", { state: "pending", idleSince: null }],
    ["unknown", { state: "unknown", idleSince: null }],
    ["not-found", { state: "not-found", idleSince: null }],
    ["idle without idleSince", { state: "idle", idleSince: null }],
    ["idle with an unparseable idleSince", { state: "idle", idleSince: "not-a-date" }],
  ])("never marks %s eligible (fail-closed)", async (_label, activity) => {
    const id = validChildSessionId(1)
    const { registry, probe } = await fixture([{ id }])
    const { probe: idleProbe } = probe(new Map([[id, activity]]))

    const result = await idleProbe.runOnce(RETENTION_7, new AbortController().signal)

    expect(result.markedEligible).toBe(0)
    expect(registry.get(id)?.phase).toBe("active")
  })

  it.each([
    ["a null policy", null],
    ["an undefined policy", undefined],
    ["a null retention window", { retentionDays: null }],
    ["a zero retention window", { retentionDays: 0 }],
    ["a negative retention window", { retentionDays: -1 }],
  ])("is fail-closed under %s even when the session is long idle", async (_label, policy) => {
    const id = validChildSessionId(1)
    const { registry, probe } = await fixture([{ id }])
    const { probe: idleProbe } = probe(new Map([[id, { state: "idle", idleSince: "2000-01-01T00:00:00.000Z" }]]))

    const result = await idleProbe.runOnce(policy as CleanupPolicy | null | undefined, new AbortController().signal)

    expect(result.markedEligible).toBe(0)
    expect(registry.get(id)?.phase).toBe("active")
  })

  it("skips already-eligible and project-less entries without querying the server", async () => {
    const eligible = validChildSessionId(1)
    const projectless = validChildSessionId(2)
    const { registry, probe } = await fixture([{ id: eligible }, { id: projectless, projectId: null }])
    await registry.markEligible(eligible)
    const { probe: idleProbe, connection } = probe(new Map())

    const result = await idleProbe.runOnce(RETENTION_7, new AbortController().signal)

    expect(result.markedEligible).toBe(0)
    expect(connection.calls).toHaveLength(0)
  })

  it("logs a probe failure and continues with the remaining entries", async () => {
    const failing = validChildSessionId(1)
    const healthy = validChildSessionId(2)
    const { registry, probe } = await fixture([{ id: failing }, { id: healthy }])
    const { probe: idleProbe } = probe(new Map<string, Response>([
      [failing, new Error("boom")],
      [healthy, { state: "idle", idleSince: "2026-01-01T00:00:00.000Z" }],
    ]))

    const result = await idleProbe.runOnce(RETENTION_7, new AbortController().signal)

    expect(result.markedEligible).toBe(1)
    expect(registry.get(failing)?.phase).toBe("active")
    expect(registry.get(healthy)?.phase).toBe("eligible")
  })

  it("is a no-op when there are no active entries", async () => {
    const { probe } = await fixture([])
    const { probe: idleProbe, connection } = probe(new Map())

    const result = await idleProbe.runOnce(RETENTION_7, new AbortController().signal)

    expect(result.markedEligible).toBe(0)
    expect(connection.calls).toHaveLength(0)
  })
})
