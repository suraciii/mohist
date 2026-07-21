/**
 * Factory seam for the OpenCode runtime.
 *
 * Mirrors `setAcpProcessFactoryForTest`: production code calls
 * `getOpenCodeRuntimeFactory()` to obtain a `OpenCodeRuntime`; tests
 * inject a fake (or a fake Client/Server pair) via
 * `setOpenCodeRuntimeFactoryForTest`.
 *
 * The factory returns a `OpenCodeRuntime` instance — the Mohist-owned
 * boundary type, not a generated SDK Client. SDK access is confined to
 * the default factory body and the test fakes the factory returns.
 */

import { OpenCodeRuntime } from "./runtime.js"
import type { OpenCodeRuntimeDeps } from "./runtime.js"
import { createSpawnedOpencodeServer } from "./server-process.js"
import { createEventSubscription } from "./event-subscription.js"

export type OpenCodeRuntimeFactory = (deps: OpenCodeRuntimeDeps) => OpenCodeRuntime

let runtimeFactory: OpenCodeRuntimeFactory = createDefaultOpenCodeRuntime

export function getOpenCodeRuntimeFactory(): OpenCodeRuntimeFactory {
  return runtimeFactory
}

export function setOpenCodeRuntimeFactoryForTest(factory: OpenCodeRuntimeFactory | null): void {
  runtimeFactory = factory ?? createDefaultOpenCodeRuntime
}

export function createDefaultOpenCodeRuntime(deps: OpenCodeRuntimeDeps): OpenCodeRuntime {
  return new OpenCodeRuntime({
    ...deps,
    serverFactory: deps.serverFactory ?? createSpawnedOpencodeServer,
    eventSubscriptionFactory: deps.eventSubscriptionFactory ?? createEventSubscription,
  })
}
