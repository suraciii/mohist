import { describe, expect, it } from 'vitest'
import { SlackAdapter } from './adapter.js'
import { HttpAdapterTransport } from './transport.js'
import { FakeSocket, FakeWeb } from './_adapterTestSupport.js'
import type { Delivery, IngressResult } from './types.js'

const target = { projectId: 'project-1', connectionId: 'connection-1' }
const event = {
  team_id: 'T1',
  api_app_id: 'A1',
  event: { type: 'message', channel: 'D1', channel_type: 'im', ts: '1710000000.000001', user: 'U1', text: 'please help' },
}

function routeHarness(result: IngressResult) {
  let durableDelivery: Delivery | null = null
  const acknowledgements: unknown[] = []
  const directResult = result
  const fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    const body = init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : {}
    if (url.endsWith('/targets')) return json([{ kind: 'connection', ...target }])
    if (url.endsWith('/acquire')) {
      const kind = body.kind
      return kind === 'validation'
        ? json({ leaseId: 'validation-lease', generation: 1, expiresAt: '2026-01-01T00:05:00Z', appToken: 'xapp', expectedAppId: 'A1' })
        : json({ leaseId: 'lease-1', generation: 1, expiresAt: '2026-01-01T00:05:00Z', appToken: 'xapp', botToken: 'xoxb' })
    }
    if (url.endsWith('/hello')) return json({ outcome: 'verified' })
    if (url.endsWith('/renew'))
      return json({ leaseId: 'lease-1', kind: 'runtime', generation: 1, expiresAt: '2026-01-01T00:05:00Z' })
    if (url.endsWith('/ingress')) return json(directResult)
    if (url.endsWith('/deliveries/claim-uncertain')) return json(null)
    if (url.endsWith('/deliveries/claim')) return json(durableDelivery)
    if (url.endsWith('/deliveries/ack')) {
      acknowledgements.push(body)
      durableDelivery = null
      return json(null)
    }
    throw new Error(`unexpected route ${url}`)
  }
  return {
    fetch,
    setDelivery(delivery: Delivery) {
      durableDelivery = delivery
    },
    acknowledgements,
  }
}

function json(data: unknown) {
  return new Response(JSON.stringify({ success: true, data }), { status: 200 })
}

async function startAdapter(harness: ReturnType<typeof routeHarness>, web: FakeWeb) {
  const transport = new HttpAdapterTransport({
    serverUrl: 'http://localhost',
    operatorToken: 'operator',
    operatorId: 'operator-1',
    fetch: harness.fetch,
  })
  const socket = new FakeSocket()
  const adapter = new SlackAdapter({
    adapterId: 'adapter-1',
    transport,
    socketFactory: () => socket,
    webFactory: () => web,
    heartbeatIntervalMs: 60_000,
    deliveryPollIntervalMs: 60_000,
  })
  const controller = new AbortController()
  await adapter.start(controller.signal)
  return { adapter, controller, socket }
}

describe('cross-component Slack ownership boundary', () => {
  it('runs a Server-owned HTTP ingress result through the real adapter handler and outbox drain once', async () => {
    const harness = routeHarness({ kind: 'agent_not_configured', responseOwner: 'server', reason: 'setup' })
    const web = new FakeWeb()
    const runtime = await startAdapter(harness, web)

    harness.setDelivery({
      id: 'nudge-1',
      conversationId: 'D1',
      threadTs: null,
      payloadJson: JSON.stringify({ operation: 'post_message', text: 'setup', clientMessageId: 'stable-nudge-1' }),
    })
    expect(await runtime.socket.emit(event)).toBe(true)
    expect(web.posted).toEqual([{ channel: 'D1', text: 'setup', client_msg_id: 'stable-nudge-1' }])
    expect(harness.acknowledgements).toHaveLength(1)
    expect(runtime.socket.acknowledgementCount).toBe(1)
    runtime.controller.abort()
    await runtime.adapter.stop()
  })

  it('runs a no-intent adapter-owned result through the real handler with one direct post and no outbox delivery', async () => {
    const harness = routeHarness({ kind: 'backpressured', responseOwner: 'adapter', reason: 'retry shortly' })
    const web = new FakeWeb()
    const runtime = await startAdapter(harness, web)

    expect(await runtime.socket.emit({
      ...event,
      event: { ...event.event, ts: '1710000000.000002' },
    })).toBe(true)

    expect(web.posted).toEqual([{ channel: 'D1', text: 'retry shortly' }])
    expect(harness.acknowledgements).toHaveLength(0)
    expect(runtime.socket.acknowledgementCount).toBe(1)
    runtime.controller.abort()
    await runtime.adapter.stop()
  })
})
