import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process'
import { createInterface, type Interface } from 'node:readline'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
interface BridgeMessage {
  readonly type: string
  readonly id?: number
  readonly url?: string
  readonly method?: string
  readonly body?: string
  readonly status?: number
  readonly responseBody?: string
  readonly acknowledged?: boolean
  readonly acknowledgementCount?: number
  readonly posted?: readonly Record<string, unknown>[]
  readonly message?: string
  readonly outboxCount?: number
  readonly nudgeCount?: number
  readonly deliveredNudgeCount?: number
  readonly inboxCount?: number
}

interface Bridge {
  readonly child: ChildProcessWithoutNullStreams
  readonly lines: Interface
  readonly messages: AsyncGenerator<BridgeMessage, void, void>
  readonly send: (message: Record<string, unknown>) => void
  readonly close: () => Promise<void>
}

const repoRoot = resolve(process.cwd(), '../..')

const event = {
  team_id: 'T-cross-component',
  api_app_id: 'A-cross-component',
  event: {
    type: 'message',
    channel: 'D1',
    channel_type: 'im',
    ts: '1710000000.000001',
    user: 'U-owner',
    text: 'please help',
  },
}

function startBridge(command: string, args: readonly string[] = []): Bridge {
  const child = spawn(command, args, { stdio: ['pipe', 'pipe', 'pipe'] })
  const lines = createInterface({ input: child.stdout })
  const messages = (async function* () {
    for await (const line of lines) {
      if (!line.trimStart().startsWith('{')) continue
      yield JSON.parse(line) as BridgeMessage
    }
  })()
  const send = (message: Record<string, unknown>) => child.stdin.write(`${JSON.stringify(message)}\n`)
  const close = async () => {
    send({ type: 'stop' })
    await new Promise<void>((resolveClose, reject) => {
      child.once('close', () => resolveClose())
      child.once('error', reject)
    })
    lines.close()
  }
  return { child, lines, messages, send, close }
}

function startServerBridge(): Bridge {
  return startBridge('dotnet', [resolve(repoRoot, 'packages/server/tests/Mohist.Server.CrossComponentBridge/bin/Debug/net11.0/Mohist.Server.CrossComponentBridge.dll')])
}

function startNodeBridge(): Bridge {
  return startBridge(process.execPath, [resolve(repoRoot, 'packages/mohist-slack/dist/cross-component-ownership-bridge.js')])
}

async function nextMessage(messages: AsyncGenerator<BridgeMessage, void, void>): Promise<BridgeMessage> {
  const result = await messages.next()
  if (result.done) throw new Error('Bridge ended unexpectedly')
  if (result.value.type === 'error') throw new Error(result.value.message ?? 'Bridge failed')
  return result.value
}

async function nextType(messages: AsyncGenerator<BridgeMessage, void, void>, type: string): Promise<BridgeMessage> {
  while (true) {
    const message = await nextMessage(messages)
    if (message.type === type) return message
  }
}

async function forwardNodeRequest(node: Bridge, server: Bridge, request: BridgeMessage): Promise<void> {
  server.send(request)
  const response = await nextType(server.messages, 'response')
  node.send(response)
}

async function startBoth(): Promise<{ node: Bridge; server: Bridge }> {
  const server = startServerBridge()
  await nextType(server.messages, 'ready')
  const node = startNodeBridge()
  while (true) {
    const message = await nextMessage(node.messages)
    if (message.type === 'ready') return { node, server }
    if (message.type === 'request') await forwardNodeRequest(node, server, message)
  }
}

async function runEvent(node: Bridge, server: Bridge, body: unknown): Promise<BridgeMessage> {
  node.send({ type: 'emit', body })
  while (true) {
    const message = await nextMessage(node.messages)
    if (message.type === 'request') {
      await forwardNodeRequest(node, server, message)
      continue
    }
    if (message.type === 'emitResult') return message
  }
}

async function setServerScenario(server: Bridge, scenario: string): Promise<void> {
  server.send({ type: 'scenario', scenario })
  await nextType(server.messages, 'scenarioApplied')
}

async function inspectServer(server: Bridge): Promise<BridgeMessage> {
  server.send({ type: 'inspect' })
  return await nextType(server.messages, 'snapshot')
}

async function closeBoth(node: Bridge, server: Bridge): Promise<void> {
  await node.close()
  await server.close()
}

describe('Server ingress and Node adapter ownership boundary', () => {
  it('passes a real Server ingress response through the real adapter and drains one durable nudge without a direct post', async () => {
    const { node, server } = await startBoth()
    try {
      await setServerScenario(server, 'server-owned')
      const result = await runEvent(node, server, event)
      const snapshot = await inspectServer(server)

      expect(result.acknowledged).toBe(true)
      expect(result.acknowledgementCount).toBe(1)
      expect(result.posted).toEqual([
        { channel: 'D1', text: expect.any(String), client_msg_id: expect.stringContaining('slack-admission-nudge:') },
      ])
      expect(snapshot.outboxCount).toBe(1)
      expect(snapshot.nudgeCount).toBe(1)
      expect(snapshot.deliveredNudgeCount).toBe(1)
      expect(snapshot.inboxCount).toBe(0)
    } finally {
      await closeBoth(node, server)
    }
  })

  it('passes a real Server backpressure response through the real adapter with one direct post and no durable nudge', async () => {
    const { node, server } = await startBoth()
    try {
      await setServerScenario(server, 'backpressure')
      const result = await runEvent(node, server, { ...event, event: { ...event.event, ts: '1710000000.000002' } })
      const snapshot = await inspectServer(server)

      expect(result.acknowledged).toBe(true)
      expect(result.acknowledgementCount).toBe(1)
      expect(result.posted).toEqual([{ channel: 'D1', text: 'This Slack Connection is temporarily busy. Please retry shortly.' }])
      expect(snapshot.nudgeCount).toBe(0)
      expect(snapshot.outboxCount).toBe(0)
      expect(snapshot.inboxCount).toBe(0)
    } finally {
      await closeBoth(node, server)
    }
  })
})
