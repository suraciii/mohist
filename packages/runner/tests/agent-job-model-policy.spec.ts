import { describe, expect, it } from "vitest"
import { AgentJobExecutor } from "../src/runtime/agent-job-executor.js"
import type { AgentJobRuntimeAccessors } from "../src/runtime/agent-job-executor.js"
import type { DispatchWorkItem } from "../src/core/types.js"
import type { ServerConnection } from "../src/server/connection.js"
import type {
  OpenCodeRuntime,
  RuntimeResult,
  RuntimeTurnRequest,
  RuntimeTurnResult,
} from "../src/runtime/opencode/index.js"

interface FakeRuntime {
  readonly runtime: OpenCodeRuntime
  readonly calls: RuntimeTurnRequest[]
  queue: (result: RuntimeResult<RuntimeTurnResult>) => void
}

function makeRuntime(): FakeRuntime {
  const calls: RuntimeTurnRequest[] = []
  const queued: RuntimeResult<RuntimeTurnResult>[] = []
  const runtime: Partial<OpenCodeRuntime> = {
    ready: () => true,
    diagnostic: () => null,
    async runTurn(request) {
      calls.push(request)
      return queued.shift() ?? successResult()
    },
  }
  return {
    runtime: runtime as OpenCodeRuntime,
    calls,
    queue(result) {
      queued.push(result)
    },
  }
}

function makeConnection(): ServerConnection {
  return { runnerId: "runner-1" } as ServerConnection
}

function makeWork(workId: string, model: string, variant: string): DispatchWorkItem {
  return {
    workflowRunId: "",
    workId,
    workType: "task",
    ownerKind: "agent-job",
    agentJobId: workId,
    agentSessionId: null,
    projectId: null,
    with: { prompt: workId, model, variant },
    variables: { workspace: { path: "/virtual/workspace" } },
  }
}

function accessors(runtime: OpenCodeRuntime): AgentJobRuntimeAccessors {
  return { openCode: runtime, pi: null }
}

function successResult(): RuntimeResult<RuntimeTurnResult> {
  return {
    ok: true,
    value: {
      facts: { finalAssistantText: "done", runtimeSessionId: "session-1", workDir: "/virtual/workspace" },
      diagnostics: [],
    },
    diagnostics: [],
  }
}

function unavailableResult(): RuntimeResult<RuntimeTurnResult> {
  const diagnostic = {
    severity: "error" as const,
    code: "model-unavailable",
    message: "The specified model is temporarily unavailable",
  }
  return {
    ok: false,
    error: { kind: "turn-failed", message: diagnostic.message, diagnostics: [diagnostic] },
    diagnostics: [diagnostic],
  }
}

describe("AgentJobExecutor model policy", () => {
  it("retries a temporarily unavailable model with the same model and variant", async () => {
    const runtime = makeRuntime()
    runtime.queue(unavailableResult())
    const delays: number[] = []
    const executor = new AgentJobExecutor(
      makeConnection(),
      accessors(runtime.runtime),
      null,
      "/virtual",
      undefined,
      null,
      {
        modelRetryInitialDelayMs: 5,
        modelRetryMaxDelayMs: 5,
        waitForModelRetry: async (delayMs, signal) => {
          delays.push(delayMs)
          return !signal.aborted
        },
      },
    )

    const work = makeWork("same-work", "opencode-go/gpt-5.6-luna", "max")
    const result = await executor.execute(work, new AbortController().signal)

    expect(result.status).toBe("completed")
    expect(delays).toEqual([5])
    expect(runtime.calls).toHaveLength(2)
    expect(runtime.calls.map((call) => call.options?.model)).toEqual([
      { providerID: "opencode-go", modelID: "gpt-5.6-luna" },
      { providerID: "opencode-go", modelID: "gpt-5.6-luna" },
    ])
    expect(runtime.calls.map((call) => call.options?.variant)).toEqual(["max", "max"])
    expect(runtime.calls.map((call) => call.prompt)).toEqual(["same-work", "same-work"])
    expect(work.workId).toBe("same-work")
  })

  it("stops waiting when the user aborts without attempting a replacement model", async () => {
    const runtime = makeRuntime()
    runtime.queue(unavailableResult())
    const controller = new AbortController()
    let markWaiting!: () => void
    const waiting = new Promise<void>((resolve) => { markWaiting = resolve })
    const executor = new AgentJobExecutor(
      makeConnection(),
      accessors(runtime.runtime),
      null,
      "/virtual",
      undefined,
      null,
      {
        waitForModelRetry: (_delayMs, signal) => new Promise<boolean>((resolve) => {
          markWaiting()
          signal.addEventListener("abort", () => resolve(false), { once: true })
        }),
      },
    )

    const execution = executor.execute(makeWork("aborted-work", "opencode-go/gpt-5.6-luna", "max"), controller.signal)
    await waiting
    controller.abort()
    const result = await execution

    expect(result.status).toBe("failed")
    expect(runtime.calls).toHaveLength(1)
  })

  it("lets another work complete while one work waits for its model", async () => {
    const runtime = makeRuntime()
    runtime.queue(unavailableResult())
    let markWaiting!: () => void
    let releaseWaiting!: () => void
    const waiting = new Promise<void>((resolve) => { markWaiting = resolve })
    const executor = new AgentJobExecutor(
      makeConnection(),
      accessors(runtime.runtime),
      null,
      "/virtual",
      undefined,
      null,
      {
        waitForModelRetry: (_delayMs, signal) => new Promise<boolean>((resolve) => {
          markWaiting()
          releaseWaiting = () => resolve(!signal.aborted)
          signal.addEventListener("abort", () => resolve(false), { once: true })
        }),
      },
    )

    const blocked = executor.execute(makeWork("blocked-work", "opencode-go/gpt-5.6-luna", "max"), new AbortController().signal)
    await waiting
    const other = await executor.execute(makeWork("other-work", "other-provider/other-model", "high"), new AbortController().signal)

    expect(other.status).toBe("completed")
    releaseWaiting()
    expect((await blocked).status).toBe("completed")
    expect(runtime.calls.map((call) => call.options?.model)).toEqual([
      { providerID: "opencode-go", modelID: "gpt-5.6-luna" },
      { providerID: "other-provider", modelID: "other-model" },
      { providerID: "opencode-go", modelID: "gpt-5.6-luna" },
    ])
    expect(runtime.calls.map((call) => call.options?.variant)).toEqual(["max", "high", "max"])
  })
})
