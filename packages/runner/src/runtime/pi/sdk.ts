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

export interface PiSdkSession {
  readonly sessionFile?: string
  readonly sessionId?: string
  readonly messages: readonly PiSdkMessage[]
  readonly isStreaming: boolean
  subscribe(listener: (event: unknown) => void): () => void
  prompt(text: string, options?: { expandPromptTemplates?: boolean }): Promise<void>
  steer(text: string): Promise<void>
  abort(): Promise<void>
  setModel(model: unknown): Promise<void>
  setThinkingLevel(level: string): void
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
        return (await createAgentSession({ cwd: sessionCwd, agentDir, modelRuntime, settingsManager, resourceLoader, sessionManager: manager, noTools: "builtin" })).session as unknown as PiSdkSession
      },
      async openSession(path, sessionCwd) {
        const manager = SessionManager.open(path, undefined, sessionCwd)
        return (await createAgentSession({ cwd: sessionCwd, agentDir, modelRuntime, settingsManager, resourceLoader, sessionManager: manager, noTools: "builtin" })).session as unknown as PiSdkSession
      },
      async close() {},
    }
  },
}

export function sdkFailure(cause: unknown): PiDiagnostic {
  return { severity: "error", code: "pi-sdk-failure", message: cause instanceof Error ? cause.message : "Pi SDK operation failed" }
}
