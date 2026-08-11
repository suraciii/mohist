import { AsyncLocalStorage } from "node:async_hooks"
import type { RunnerResourceContext } from "../../src/system/filesystem.js"
import { withTestRunnerResources } from "./test-resources.js"

export interface SignalRTestState {
  readonly builders: unknown[]
  nextConnectionId: number
}

const signalRTestStorage = new AsyncLocalStorage<SignalRTestState>()

export function currentSignalRTestState(): SignalRTestState {
  const state = signalRTestStorage.getStore()
  if (!state) throw new Error("SignalR test resource context is not active")
  return state
}

export async function withSignalRTestResources<T>(
  resources: Omit<RunnerResourceContext, "fileSystem"> & { fileSystem?: RunnerResourceContext["fileSystem"] },
  body: () => Promise<T>,
): Promise<T> {
  return await withTestRunnerResources(
    async () => await signalRTestStorage.run({ builders: [], nextConnectionId: 0 }, body),
    resources,
  )
}
