/**
 * Factory seam for the Codex runtime.
 *
 * Production code calls `getCodexRuntimeFactory()` to obtain a
 * `CodexRuntimeFactory`; tests inject a fake (or a fake
 * `CodexServerFactory`) through the active resource context. The
 * factory returns a `CodexRuntime` instance — the Mohist-owned boundary
 * type, not a generated SDK Client. App-server protocol access is
 * confined to the default factory body and the test fakes the factory
 * returns.
 *
 * The default factory wires the line-framed JSON-RPC consumer
 * (`createSpawnedCodexServer`) and the locked v2 protocol subset
 * (`./protocol-types.ts`). Tests may inject their own server factory
 * via `currentRunnerResources().codexServerFactory` or directly
 * through `CodexRuntimeDeps.serverFactory`.
 */

import { CodexRuntime, type CodexRuntimeDeps } from './runtime.js'
import { createSpawnedCodexServer, type CodexServerFactory } from './server-process.js'
import { currentRunnerResources } from '../../system/filesystem.js'

export type CodexRuntimeFactory = (deps: CodexRuntimeDeps) => CodexRuntime

export function getCodexRuntimeFactory(): CodexRuntimeFactory {
  return currentRunnerResources()?.codexRuntimeFactory ?? createDefaultCodexRuntime
}

/**
 * Default Codex runtime factory body.
 *
 * - injects the spawned line-framed JSON-RPC consumer
 *   (`createSpawnedCodexServer`) when the caller did not supply a
 *   custom `serverFactory`;
 * - threads the same `codexHome` / `cwd` into the process boundary;
 * - lets the runtime own the locked v2 protocol subset declared in
 *   `./protocol-types.ts` (the consumer refuses to send methods
 *   outside that subset and rejects malformed envelopes).
 */
export function createDefaultCodexRuntime(deps: CodexRuntimeDeps): CodexRuntime {
  const serverFactory: CodexServerFactory = deps.serverFactory ?? createSpawnedCodexServer
  return new CodexRuntime({
    ...deps,
    serverFactory,
  })
}

/**
 * Resource context lookup used by tests and by `RunnerHost`. The
 * resource context may provide either a fully built `CodexRuntime`
 * factory (preferred for integration tests) or just a
 * `codexServerFactory` (preferred for unit tests that exercise the
 * runtime against a fake consumer).
 */
export function getCodexServerFactory(): CodexServerFactory | undefined {
  return currentRunnerResources()?.codexServerFactory
}