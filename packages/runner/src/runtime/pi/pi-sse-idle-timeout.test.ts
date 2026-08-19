import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest'
import { readFileSync, writeFileSync, existsSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
// @ts-expect-error the patch script ships without type declarations
import { applyIdleTimeoutPatch } from '../../../scripts/patch-pi-idle-timeout.mjs'

function findUp(startDir: string, name: string): string {
  let dir = startDir
  for (;;) {
    const candidate = join(dir, name)
    if (existsSync(candidate)) return candidate
    const parent = dirname(dir)
    if (parent === dir) throw new Error(`could not find ${name} above ${startDir}`)
    dir = parent
  }
}

const piCodingAgentRoot = findUp(
  dirname(fileURLToPath(import.meta.url)),
  'node_modules/@earendil-works/pi-coding-agent',
)
const SSE_TARGET = join(piCodingAgentRoot, 'node_modules/@earendil-works/pi-ai/dist/api/anthropic-messages.js')
const ORIGINAL = readFileSync(SSE_TARGET, 'utf8')

const IDLE_TIMEOUT_MS = 3_000

type StreamEvent = {
  type: string
  reason?: string
  error?: { errorMessage?: string }
}
type StreamFn = (model: unknown, context: unknown, options: { client: unknown }) => AsyncIterable<StreamEvent>

let stream: StreamFn

const sse = (event: string, data: unknown) => `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`

const payload = [
  sse('message_start', {
    type: 'message_start',
    message: {
      id: 'msg_1',
      type: 'message',
      role: 'assistant',
      model: 'test',
      content: [],
      stop_reason: null,
      stop_sequence: null,
      usage: { input_tokens: 5, output_tokens: 0 },
    },
  }),
  sse('content_block_start', { type: 'content_block_start', index: 0, content_block: { type: 'text', text: '' } }),
  sse('content_block_delta', { type: 'content_block_delta', index: 0, delta: { type: 'text_delta', text: 'hi' } }),
  sse('content_block_stop', { type: 'content_block_stop', index: 0 }),
  sse('message_delta', {
    type: 'message_delta',
    delta: { stop_reason: 'end_turn', stop_sequence: null },
    usage: { output_tokens: 5 },
  }),
  sse('message_stop', { type: 'message_stop' }),
].join('')

const model = {
  provider: 'test',
  id: 'test-model',
  api: { endpoint: 'http://localhost', key: 'k' },
  maxTokens: 1000,
  input: ['text'],
  cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0, tiers: [] },
}
const context = {
  messages: [{ role: 'user', content: [{ type: 'text', text: 'hi' }] }],
  systemPrompt: 'sys',
  tools: [],
}

const keepAliveTimers = new Set<ReturnType<typeof setInterval>>()

type BodyMode = 'normal' | 'stall' | 'keepalive'
function makeBody(mode: BodyMode): ReadableStream {
  return new ReadableStream({
    start(controller) {
      controller.enqueue(new TextEncoder().encode(payload))
      if (mode === 'normal') {
        controller.close()
      } else if (mode === 'keepalive') {
        const timer = setInterval(() => {
          controller.enqueue(new TextEncoder().encode(': keep-alive\n\n'))
        }, 20)
        keepAliveTimers.add(timer)
      }
      // "stall": never close, never push again
    },
  })
}

async function run(mode: BodyMode): Promise<{ last: string | undefined; seen: string[]; error?: string }> {
  const body = makeBody(mode)
  const client = {
    messages: {
      create: () => ({
        asResponse: async () => ({ status: 200, headers: new Headers(), body }),
      }),
    },
  }
  const events = stream(model, context, { client })
  const seen: string[] = []
  let lastError: string | undefined
  for await (const event of events) {
    seen.push(event.type)
    if (event.type === 'error') {
      lastError = event.error?.errorMessage
    }
    if (event.type === 'done' || event.type === 'error') {
      break
    }
  }
  return { last: seen[seen.length - 1], seen, error: lastError }
}

async function runUntilIdleTimeout(mode: Exclude<BodyMode, 'normal'>) {
  vi.useFakeTimers()
  try {
    const result = run(mode)
    await vi.advanceTimersByTimeAsync(IDLE_TIMEOUT_MS + 1)
    return await result
  } finally {
    vi.clearAllTimers()
    vi.useRealTimers()
  }
}

beforeAll(async () => {
  // The patched reader resolves its timeout from this env var at module
  // load; set it before importing so both freshly-patched and
  // already-patched (postbuild) trees use the short test timeout.
  process.env.PI_SSE_IDLE_TIMEOUT_MS = String(IDLE_TIMEOUT_MS)
  const result = applyIdleTimeoutPatch(SSE_TARGET, IDLE_TIMEOUT_MS)
  // The postbuild step may have already patched the tree; both states carry
  // the patched behavior under test.
  if (!result.applied && result.reason !== 'already-applied') {
    throw new Error(`patch failed: ${result.reason ?? 'unknown'}`)
  }
  const mod = (await import(`${pathToFileURL(SSE_TARGET).href}?idle=${Date.now()}`)) as { stream: StreamFn }
  stream = mod.stream
})

afterAll(() => {
  for (const timer of keepAliveTimers) {
    clearInterval(timer)
  }
  keepAliveTimers.clear()
  delete process.env.PI_SSE_IDLE_TIMEOUT_MS
  writeFileSync(SSE_TARGET, ORIGINAL)
})

describe('pi-ai SSE no-event idle timeout patch', () => {
  it('lets a normal full stream complete with done', async () => {
    const result = await run('normal')
    expect(result.last).toBe('done')
  }, 15_000)

  it('settles a stalled stream (no data after payload) with an idle-timeout error', async () => {
    const result = await runUntilIdleTimeout('stall')
    expect(result.last).toBe('error')
    expect(result.error).toContain('idle timeout')
  }, 15_000)

  it('settles a keep-alive stream (comment lines, no events) with an idle-timeout error', async () => {
    const result = await runUntilIdleTimeout('keepalive')
    expect(result.last).toBe('error')
    expect(result.error).toContain('idle timeout')
  }, 15_000)
})
