import { describe, expect, it } from 'vitest'
import { PiRuntime, type PiSdkFactory, type PiSdkServices, type PiSdkSession } from '../src/runtime/pi/index.js'

class CarrySession implements PiSdkSession {
  sessionFile: string
  readonly sessionId = 'carry-session'
  readonly messages: PiSdkSession['messages'] = []
  readonly isStreaming = false
  readonly thinkingCalls: string[] = []
  currentThinkingLevel = 'off'

  constructor(sessionFile: string) {
    this.sessionFile = sessionFile
  }

  subscribe(): () => void {
    return () => undefined
  }

  prompt(): Promise<void> {
    return Promise.resolve()
  }

  steer(): Promise<void> {
    return Promise.resolve()
  }

  abort(): Promise<void> {
    return Promise.resolve()
  }

  compact(): Promise<void> {
    return Promise.resolve()
  }

  setModel(): Promise<void> {
    return Promise.resolve()
  }

  setThinkingLevel(level: string): void {
    this.thinkingCalls.push(level)
    this.currentThinkingLevel = level
  }

  getModel(): unknown {
    return undefined
  }

  getThinkingLevel(): string {
    return this.currentThinkingLevel
  }

  dispose(): void {}
}

describe('PiRuntime effort carry-over', () => {
  it('carries effort-derived thinking through reset without using the variant', async () => {
    const prior = new CarrySession('/workspace/prior.jsonl')
    const next = new CarrySession('/workspace/next.jsonl')
    const services: PiSdkServices = {
      catalog: async () => [{ provider: 'fake', id: 'model', thinkingLevels: ['high'] }],
      createSession: async () => next,
      openSession: async (path) => {
        expect(path).toBe(prior.sessionFile)
        return prior
      },
      model: () => undefined,
      close: async () => undefined,
    }
    const runtime = new PiRuntime({ agentDir: '/agent', sdkFactory: { create: async () => services } as PiSdkFactory })
    await runtime.start()

    const turn = await runtime.runTurn(
      {
        target: { runtime: 'pi', runtimeSessionId: prior.sessionFile, workDir: '/workspace' },
        prompt: 'apply effort',
        options: { variant: 'max', reasoningEffort: 'high' },
      },
      new AbortController().signal,
    )
    expect(turn.ok).toBe(true)

    const reset = await runtime.reset({
      target: { runtime: 'pi', runtimeSessionId: prior.sessionFile, workDir: '/workspace' },
    })

    expect(reset.ok).toBe(true)
    expect(prior.thinkingCalls).toEqual(['high'])
    expect(next.thinkingCalls).toEqual(['high'])
  })
})
