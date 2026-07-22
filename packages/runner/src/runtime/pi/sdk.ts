import {
  createAgentSession,
  DefaultResourceLoader,
  ModelRuntime,
  SessionManager,
  SettingsManager,
} from "@earendil-works/pi-coding-agent"
import type { PiDiagnostic } from "./types.js"

export interface PiSdkMessage {
  readonly role?: string
  readonly content?: unknown
  readonly stopReason?: string
  readonly errorMessage?: string
  readonly usage?: Record<string, unknown>
}

/**
 * Reception callback for an idle `prompt()` call (issue #451 / design D5).
 *
 * Mirrors `preflightResult` on the Pi SDK's `PromptOptions`. The SDK
 * invokes it with `true` once the prompt has passed preflight validation
 * (model selected, credentials present, no streaming conflict) and is
 * about to start the underlying agent loop, and with `false` when the
 * preflight rejects the prompt (missing model, missing credentials,
 * streaming without `streamingBehavior`). The runner resolves an idle
 * Follow-up on `true` and fails it on `false` — no automatic retry
 * (`design/runtimes/pi.md` D5).
 */
export type PiPromptPreflightResult = (success: boolean) => void

export interface PiPromptOptions {
  readonly expandPromptTemplates?: boolean
  readonly preflight?: PiPromptPreflightResult
}

export interface PiSdkSession {
  readonly sessionFile?: string
  readonly sessionId?: string
  readonly messages: readonly PiSdkMessage[]
  readonly isStreaming: boolean
  subscribe(listener: (event: unknown) => void): () => void
  prompt(text: string, options?: PiPromptOptions): Promise<void>
  steer(text: string): Promise<void>
  abort(): Promise<void>
  compact(): Promise<void>
  setModel(model: unknown): Promise<void>
  setThinkingLevel(level: string): void
  /**
   * Read the currently selected Pi model, or `undefined` if no model
   * has been selected yet on this session. Mirrors
   * `AgentSession.model` (issue #451 / design D8).
   *
   * The returned object is opaque to the boundary — the caller passes
   * it back through `setModel` to apply it onto another session.
   */
  getModel(): unknown
  /**
   * Read the current Pi thinking level (e.g. `"off"`, `"medium"`,
   * `"high"`). Mirrors `AgentSession.thinkingLevel` (issue #451 /
   * design D8). Always returns a non-empty string; the SDK defaults
   * to `"off"` when no level has been set explicitly.
   */
  getThinkingLevel(): string
  dispose(): void
}

export interface PiSdkFactoryOptions {
  readonly cwd: string
  readonly agentDir: string
}

export interface PiSdkServices {
  catalog(): Promise<readonly { readonly provider: string; readonly id: string; readonly thinkingLevels?: readonly string[] }[]>
  createSession(cwd: string): Promise<PiSdkSession>
  openSession(path: string, cwd: string): Promise<PiSdkSession>
  model(provider: string, id: string): unknown
  close(): Promise<void>
}

export interface PiSdkFactory {
  create(options: PiSdkFactoryOptions): Promise<PiSdkServices>
}

export const realPiSdkFactory: PiSdkFactory = {
  async create({ cwd, agentDir }) {
    const settingsManager = SettingsManager.create(cwd, agentDir, { projectTrusted: false })
    const modelRuntime = await ModelRuntime.create()
    const resourceLoader = new DefaultResourceLoader({ cwd, agentDir, settingsManager })
    await resourceLoader.reload()
    return {
      async catalog() {
        return (await modelRuntime.getAvailable()).map((model) => ({
          provider: model.provider,
          id: model.id,
          thinkingLevels: model.reasoning ? ["minimal", "low", "medium", "high", "xhigh", "max"] : ["off"],
        }))
      },
      model(provider, id) {
        const model = modelRuntime.getModel(provider, id)
        if (!model) throw new Error(`Pi model ${provider}/${id} is unavailable`)
        return model
      },
      async createSession(sessionCwd) {
        const manager = SessionManager.create(sessionCwd)
        const session = (await createAgentSession({ cwd: sessionCwd, agentDir, modelRuntime, settingsManager, resourceLoader, sessionManager: manager, noTools: "builtin" })).session
        return wrapAgentSession(session)
      },
      async openSession(path, sessionCwd) {
        const manager = SessionManager.open(path, undefined, sessionCwd)
        const session = (await createAgentSession({ cwd: sessionCwd, agentDir, modelRuntime, settingsManager, resourceLoader, sessionManager: manager, noTools: "builtin" })).session
        return wrapAgentSession(session)
      },
      async close() {},
    }
  },
}

export function sdkFailure(cause: unknown): PiDiagnostic {
  return { severity: "error", code: "pi-sdk-failure", message: cause instanceof Error ? cause.message : "Pi SDK operation failed" }
}

/**
 * Wrap a real `AgentSession` so it satisfies the `PiSdkSession`
 * boundary. The wrapper preserves the SDK's identity, so the
 * session-mutex keyed by `sessionFile` continues to point at the
 * same underlying agent even when getter fields are read.
 *
 * `getModel` and `getThinkingLevel` read directly from the live
 * `AgentSession` so Reset can carry the current model/thinking
 * level onto a freshly created session (issue #451 / design D8).
 *
 * The non-getter members (`sessionFile`, `sessionId`, `messages`,
 * `isStreaming`, `subscribe`, `prompt`, `steer`, `abort`, `compact`,
 * `setModel`, `setThinkingLevel`, `dispose`) are exposed as forwarders
 * rather than a value-spread so the AgentSession getters stay live
 * (a value-spread would snapshot them at wrap time).
 */
function wrapAgentSession(session: unknown): PiSdkSession {
  const agent = session as PiSdkSession & { readonly model?: unknown; readonly thinkingLevel?: string }
  return {
    get sessionFile() { return agent.sessionFile },
    get sessionId() { return agent.sessionId },
    get messages() { return agent.messages },
    get isStreaming() { return agent.isStreaming },
    subscribe: (listener) => agent.subscribe(listener),
    prompt: (text, options) => agent.prompt(text, options),
    steer: (text) => agent.steer(text),
    abort: () => agent.abort(),
    compact: () => agent.compact(),
    setModel: (model) => agent.setModel(model),
    setThinkingLevel: (level) => agent.setThinkingLevel(level),
    getModel: () => agent.model,
    getThinkingLevel: () => agent.thinkingLevel ?? "off",
    dispose: () => agent.dispose(),
  }
}
