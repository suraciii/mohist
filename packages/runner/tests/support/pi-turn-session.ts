import { vi } from 'vitest'
import type { PiSdkFactory, PiSdkSession } from '../../src/runtime/pi/index.js'
import { deferred } from './deferred.js'

export interface PiTestMessage {
  role: string
  content: unknown
  stopReason?: string
  errorMessage?: string
}

export class ControlledPiSession implements PiSdkSession {
  readonly sessionFile = '/virtual/sessions/controlled.jsonl'
  readonly sessionId = 'controlled-session'
  messages: PiTestMessage[] = []
  isStreaming = false
  readonly listeners = new Set<(event: unknown) => void>()
  readonly promptEntered = deferred()
  readonly promptCompletion = deferred()
  readonly prompt = vi.fn((text: string) => {
    this.messages.push({ role: 'user', content: text })
    this.isStreaming = true
    this.promptEntered.resolve()
    return this.promptCompletion.promise
  })
  readonly abort = vi.fn(async () => {
    this.isStreaming = false
  })
  readonly steer = vi.fn(async () => {})
  readonly compact = vi.fn(async () => {})
  readonly setModel = vi.fn(async (_model: unknown) => {})
  readonly setThinkingLevel = vi.fn((_level: string) => {})
  readonly dispose = vi.fn()

  subscribe(listener: (event: unknown) => void): () => void {
    this.listeners.add(listener)
    return () => {
      this.listeners.delete(listener)
    }
  }
  emit(event: unknown): void {
    this.listeners.forEach((listener) => listener(event))
  }
  getModel(): unknown {
    return undefined
  }
  getThinkingLevel(): string {
    return 'off'
  }
}

export function controlledPiSdk(session: ControlledPiSession): PiSdkFactory {
  return {
    create: async () => ({
      catalog: async () => [{ provider: 'test', id: 'luna', thinkingLevels: ['high'] }],
      createSession: async () => session,
      openSession: async () => session,
      model: (provider, id) => ({ provider, id }),
      close: async () => {},
    }),
  }
}
