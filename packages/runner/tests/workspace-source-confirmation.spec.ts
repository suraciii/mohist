import { join } from "node:path"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { AgentJobExecutor } from "../src/runtime/agent-job-executor.js"
import { AgentWorkspaceManager } from "../src/runtime/agent-workspace.js"
import { AgentWorkspaceRegistry } from "../src/runtime/agent-workspace-registry.js"
import { WorkspaceRegistry } from "../src/runtime/workspace-registry.js"
import { WorkspaceSourceConfirmer, type WorkspaceSourceReporter } from "../src/runtime/workspace-source.js"
import type { DispatchWorkItem } from "../src/core/types.js"
import * as processModule from "../src/system/process.js"
import {
  createAgentManager,
  createRunnerOwnedParent,
  createSymlinkedDir,
  FakeAgentGit,
  validChildSessionId,
} from "./support/agent-workspace-fixture.js"
import { createTestTempDir } from "./support/temp-dir.js"

const GIT_URL = "https://example.test/mohist.git"

let fake: FakeAgentGit
let restoreRunCommand: (() => void) | undefined

beforeEach(() => {
  fake = new FakeAgentGit()
  const spy = vi.spyOn(processModule, "runCommand").mockImplementation((command, args, cwd, signal, env, options) => {
    return fake.run(command, args, cwd, signal, env, options)
  })
  restoreRunCommand = () => spy.mockRestore()
})

afterEach(() => {
  restoreRunCommand?.()
  restoreRunCommand = undefined
})

interface ReporterSpy {
  confirmed: ReturnType<typeof vi.fn>
  rejected: ReturnType<typeof vi.fn>
  reporter: WorkspaceSourceReporter
}

function reporterSpy(): ReporterSpy {
  const confirmed = vi.fn(async () => undefined)
  const rejected = vi.fn(async () => undefined)
  return {
    confirmed,
    rejected,
    reporter: {
      reportConfirmed: confirmed,
      reportRejected: rejected,
    },
  }
}

function request(workDir: string, sessionId = "session-1") {
  return {
    sessionId,
    workDir,
    repository: { name: "main", gitUrl: GIT_URL, baseBranch: "master" },
  }
}

describe("WorkspaceSourceConfirmer", () => {
  it("Confirmed_WhenOwnedAndOriginMatches_ReportsOnceAndCaches", async () => {
    const root = await createTestTempDir("mohist-source-")
    const workflowRegistry = new WorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await workflowRegistry.load()
    const registry = new AgentWorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await registry.load()
    const manager = createAgentManager(root, registry, fake, { workflowRegistry })
    const parentPath = await createRunnerOwnedParent(root, workflowRegistry, fake)
    const spy = reporterSpy()
    const confirmer = new WorkspaceSourceConfirmer(manager, spy.reporter)

    const first = await confirmer.confirm(request(parentPath), new AbortController().signal)
    const second = await confirmer.confirm(request(parentPath), new AbortController().signal)

    expect(first).toEqual({ kind: "confirmed" })
    expect(second).toEqual({ kind: "confirmed" })
    expect(spy.confirmed).toHaveBeenCalledTimes(1)
    expect(spy.confirmed).toHaveBeenCalledWith(request(parentPath), expect.any(AbortSignal))
    expect(spy.rejected).not.toHaveBeenCalled()
  })

  it("OriginMismatch_ReportsRejectedWithOriginMismatch", async () => {
    const root = await createTestTempDir("mohist-source-")
    const workflowRegistry = new WorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await workflowRegistry.load()
    const registry = new AgentWorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await registry.load()
    const manager = createAgentManager(root, registry, fake, { workflowRegistry })
    const parentPath = await createRunnerOwnedParent(root, workflowRegistry, fake)
    fake.origins.set(parentPath, "https://example.test/other.git")
    const spy = reporterSpy()
    const confirmer = new WorkspaceSourceConfirmer(manager, spy.reporter)

    const result = await confirmer.confirm(request(parentPath), new AbortController().signal)

    expect(result).toEqual({ kind: "rejected", reason: "origin-mismatch" })
    expect(spy.rejected).toHaveBeenCalledWith(request(parentPath), "origin-mismatch", expect.any(AbortSignal))
  })

  it("UnregisteredWorkDir_ReportsRejectedWithNotRunnerOwned", async () => {
    const root = await createTestTempDir("mohist-source-")
    const workflowRegistry = new WorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await workflowRegistry.load()
    const registry = new AgentWorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await registry.load()
    const manager = createAgentManager(root, registry, fake, { workflowRegistry })
    const rogue = join(root, "rogue")
    const spy = reporterSpy()
    const confirmer = new WorkspaceSourceConfirmer(manager, spy.reporter)

    const result = await confirmer.confirm(request(rogue), new AbortController().signal)

    expect(result).toEqual({ kind: "rejected", reason: "not-runner-owned" })
    expect(spy.rejected).toHaveBeenCalledWith(request(rogue), "not-runner-owned", expect.any(AbortSignal))
  })

  it("SymlinkedWorkDir_ReportsRejectedWithNotRunnerOwned", async () => {
    const root = await createTestTempDir("mohist-source-")
    const workflowRegistry = new WorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await workflowRegistry.load()
    const registry = new AgentWorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await registry.load()
    const manager = createAgentManager(root, registry, fake, { workflowRegistry })
    const linked = join(root, "workspaces", "wr-linked")
    await createSymlinkedDir(join(root, "real"), linked)
    const spy = reporterSpy()
    const confirmer = new WorkspaceSourceConfirmer(manager, spy.reporter)

    const result = await confirmer.confirm(request(linked), new AbortController().signal)

    expect(result).toEqual({ kind: "rejected", reason: "not-runner-owned" })
  })

  it("FailedReport_IsNotCached_AndRetriesOnTheNextExecution", async () => {
    const root = await createTestTempDir("mohist-source-")
    const workflowRegistry = new WorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await workflowRegistry.load()
    const registry = new AgentWorkspaceRegistry(root, { now: () => new Date("2026-01-01T00:00:00.000Z") })
    await registry.load()
    const manager = createAgentManager(root, registry, fake, { workflowRegistry })
    const parentPath = await createRunnerOwnedParent(root, workflowRegistry, fake)
    const confirmed = vi.fn(async () => {
      if (confirmed.mock.calls.length === 1) throw new Error("transport down")
    })
    const confirmer = new WorkspaceSourceConfirmer(manager, { reportConfirmed: confirmed, reportRejected: async () => undefined })

    const first = await confirmer.confirm(request(parentPath), new AbortController().signal)
    const second = await confirmer.confirm(request(parentPath), new AbortController().signal)

    expect(first).toEqual({ kind: "confirmed" })
    expect(second).toEqual({ kind: "confirmed" })
    expect(confirmed).toHaveBeenCalledTimes(2)
  })
})

describe("AgentJobExecutor source-confirmation hook", () => {
  it("DefersConfirmationUntilTheRuntimeSessionAttaches_WhenTheTurnFailsBeforeAttach", async () => {
    const confirmer = { confirm: vi.fn(async () => ({ kind: "confirmed" as const })) } as unknown as WorkspaceSourceConfirmer
    const executor = new AgentJobExecutor({} as never, { openCode: null, pi: null }, null, "/fallback", undefined, confirmer)
    const work: DispatchWorkItem = {
      ownerKind: "agent-job",
      workflowRunId: "wr-agent",
      workId: "job.1",
      workType: "task",
      agentJobId: "job-1",
      with: { prompt: "do the thing" },
      variables: { workspace: { path: "/owned/workdir" } },
      agentSessionStartup: {
        projectId: "project-1",
        sessionId: "session-9",
        spawnCommand: "spawn",
        allowedSubagents: [],
        workspaceRepository: { name: "main", gitUrl: GIT_URL, baseBranch: "master" },
      },
    }

    const result = await executor.execute(work, new AbortController().signal)

    // Confirmation runs only after the runtime session attaches; a turn
    // that fails before attach must not report a verdict.
    expect(result.status).toBe("failed")
    expect(confirmer.confirm).not.toHaveBeenCalled()
  })

  it("SkipsConfirmation_WhenTheStartupHasNoRepositorySnapshot", async () => {
    const confirmer = { confirm: vi.fn(async () => ({ kind: "confirmed" as const })) } as unknown as WorkspaceSourceConfirmer
    const executor = new AgentJobExecutor({} as never, { openCode: null, pi: null }, null, "/fallback", undefined, confirmer)
    const work: DispatchWorkItem = {
      ownerKind: "agent-job",
      workflowRunId: "wr-agent",
      workId: "job.1",
      workType: "task",
      agentJobId: "job-1",
      with: { prompt: "do the thing" },
      variables: { workspace: { path: "/owned/workdir" } },
      agentSessionStartup: {
        projectId: "project-1",
        sessionId: "session-9",
        spawnCommand: "spawn",
        allowedSubagents: [],
      },
    }

    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("failed")
    expect(confirmer.confirm).not.toHaveBeenCalled()
  })
})
