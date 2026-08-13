import {
  createAgentSession,
  DefaultResourceLoader,
  ModelRuntime,
  SessionManager,
  SettingsManager,
} from "@earendil-works/pi-coding-agent"
import { resolve } from "node:path"
import type { PiDiagnostic } from "./types.js"

export interface PiSdkMessage {
  readonly role?: string
  readonly content?: unknown
  readonly stopReason?: string
  readonly errorMessage?: string
  readonly usage?: Record<string, unknown>
}

/**
 * Reception callback for an idle `prompt()` call.
 *
 * Mirrors `preflightResult` on the Pi SDK's `PromptOptions`. The SDK
 * invokes it with `true` once the prompt has passed preflight validation
 * (model selected, credentials present, no streaming conflict) and is
 * about to start the underlying agent loop, and with `false` when the
 * preflight rejects the prompt (missing model, missing credentials,
 * streaming without `streamingBehavior`). The runner resolves an idle
 * Follow-up on `true` and fails it on `false` — no automatic retry.
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
   * `AgentSession.model`.
   *
   * The returned object is opaque to the boundary — the caller passes
   * it back through `setModel` to apply it onto another session.
   */
  getModel(): unknown
  /**
   * Read the current Pi thinking level (e.g. `"off"`, `"medium"`,
   * `"high"`). Mirrors `AgentSession.thinkingLevel`
   *. Always returns a non-empty string; the SDK defaults
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
  validateSessionFile?(path: string, expectedSessionId?: string): Promise<void>
  model(provider: string, id: string): unknown
  close(): Promise<void>
}

export interface PiSdkFactory {
  create(options: PiSdkFactoryOptions): Promise<PiSdkServices>
}

export function validatePiSessionContents(content: string, expectedSessionId?: string): { readonly entryCount: number; readonly sessionId: string } {
  const entries = content
    .split("\n")
    .filter((line) => line.trim().length > 0)
    .map((line, index) => parseEntry(line, index + 1))
  const [header, ...sessionEntries] = entries
  if (!header || header.type !== "session") throw new Error("Pi session file must begin with a session header")
  const sessionId = requiredString(header, "id", "session header")
  if (expectedSessionId !== undefined && sessionId !== expectedSessionId) throw new Error("Pi session file has an unexpected session id")
  requiredTimestamp(header, "timestamp", "session header")
  requiredString(header, "cwd", "session header")
  if ("version" in header && (!Number.isInteger(header.version) || (header.version as number) < 1)) {
    throw new Error("Pi session header has an invalid version")
  }

  const entryIds = new Set<string>()
  for (const entry of sessionEntries) validateSessionEntry(entry, entryIds)
  return { entryCount: sessionEntries.length, sessionId }
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
        const session = (await createAgentSession({ cwd: sessionCwd, agentDir, modelRuntime, settingsManager, resourceLoader, sessionManager: manager })).session
        return wrapAgentSession(session)
      },
      async openSession(path, sessionCwd) {
        const manager = SessionManager.open(path, undefined, sessionCwd)
        const session = (await createAgentSession({ cwd: sessionCwd, agentDir, modelRuntime, settingsManager, resourceLoader, sessionManager: manager })).session
        return wrapAgentSession(session)
      },
      async validateSessionFile(path, expectedSessionId) {
        const { readFile } = await import("node:fs/promises")
        const content = await readFile(path, "utf8")
        const validation = validatePiSessionContents(content, expectedSessionId)
        const manager = SessionManager.open(path)
        if (
          manager.getSessionFile() !== resolve(path)
          || manager.getSessionId() !== validation.sessionId
          || (expectedSessionId !== undefined && manager.getSessionId() !== expectedSessionId)
          || manager.getEntries().length !== validation.entryCount
        ) throw new Error("Pi session file could not be restored by SessionManager")
      },
      async close() {},
    }
  },
}

export function sdkFailure(cause: unknown): PiDiagnostic {
  return { severity: "error", code: "pi-sdk-failure", message: cause instanceof Error ? cause.message : "Pi SDK operation failed" }
}

function parseEntry(line: string, lineNumber: number): Record<string, unknown> {
  let entry: unknown
  try { entry = JSON.parse(line) } catch { throw new Error(`Pi session file contains invalid JSON at line ${lineNumber}`) }
  if (!isRecord(entry)) throw new Error(`Pi session file contains a non-object entry at line ${lineNumber}`)
  return entry
}

function validateSessionEntry(entry: Record<string, unknown>, entryIds: Set<string>): void {
  const type = requiredString(entry, "type", "session entry")
  const id = requiredString(entry, "id", "session entry")
  if (entryIds.has(id)) throw new Error(`Pi session file contains duplicate entry id ${id}`)
  const parentId = entry.parentId
  if (parentId !== null && typeof parentId !== "string") throw new Error(`Pi session entry ${id} has an invalid parentId`)
  if (typeof parentId === "string" && !entryIds.has(parentId)) throw new Error(`Pi session entry ${id} has an unknown parentId`)
  requiredTimestamp(entry, "timestamp", `session entry ${id}`)

  switch (type) {
    case "message":
      if (!isRecord(entry.message) || typeof entry.message.role !== "string") throw new Error(`Pi session message ${id} is invalid`)
      break
    case "thinking_level_change":
      requiredString(entry, "thinkingLevel", `session entry ${id}`)
      break
    case "model_change":
      requiredString(entry, "provider", `session entry ${id}`)
      requiredString(entry, "modelId", `session entry ${id}`)
      break
    case "compaction":
      requiredString(entry, "summary", `session entry ${id}`)
      requireKnownEntry(entry, "firstKeptEntryId", entryIds, id)
      if (typeof entry.tokensBefore !== "number") throw new Error(`Pi session compaction ${id} has invalid tokensBefore`)
      break
    case "branch_summary":
      requireKnownEntry(entry, "fromId", entryIds, id)
      requiredString(entry, "summary", `session entry ${id}`)
      break
    case "custom":
      requiredString(entry, "customType", `session entry ${id}`)
      break
    case "custom_message":
      requiredString(entry, "customType", `session entry ${id}`)
      if (typeof entry.content !== "string" && !Array.isArray(entry.content)) throw new Error(`Pi session custom message ${id} has invalid content`)
      if (typeof entry.display !== "boolean") throw new Error(`Pi session custom message ${id} has invalid display`)
      break
    case "label":
      requireKnownEntry(entry, "targetId", entryIds, id)
      if ("label" in entry && entry.label !== undefined && typeof entry.label !== "string") throw new Error(`Pi session label ${id} is invalid`)
      break
    case "session_info":
      if ("name" in entry && entry.name !== undefined && typeof entry.name !== "string") throw new Error(`Pi session info ${id} is invalid`)
      break
    default:
      throw new Error(`Pi session entry ${id} has an unsupported type ${type}`)
  }
  entryIds.add(id)
}

function requireKnownEntry(entry: Record<string, unknown>, field: string, entryIds: Set<string>, entryId: string): void {
  const targetId = requiredString(entry, field, `session entry ${entryId}`)
  if (!entryIds.has(targetId)) throw new Error(`Pi session entry ${entryId} has an unknown ${field}`)
}

function requiredTimestamp(entry: Record<string, unknown>, field: string, context: string): void {
  const value = requiredString(entry, field, context)
  if (Number.isNaN(Date.parse(value))) throw new Error(`${context} has an invalid ${field}`)
}

function requiredString(entry: Record<string, unknown>, field: string, context: string): string {
  const value = entry[field]
  if (typeof value !== "string" || value.length === 0) throw new Error(`${context} has an invalid ${field}`)
  return value
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value)
}

/**
 * Wrap a real `AgentSession` so it satisfies the `PiSdkSession`
 * boundary. The wrapper preserves the SDK's identity, so the
 * session-mutex keyed by `sessionFile` continues to point at the
 * same underlying agent even when getter fields are read.
 *
 * `getModel` and `getThinkingLevel` read directly from the live
 * `AgentSession` so Reset can carry the current model/thinking
 * level onto a freshly created session.
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
