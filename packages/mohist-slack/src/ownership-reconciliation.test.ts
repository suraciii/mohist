import { describe, expect, it } from 'vitest'
import { SlackAdapter } from './adapter.js'
import { FakeSocket, FakeTransport, FakeWeb } from './_adapterTestSupport.js'

const event = {
  team_id: 'T1',
  api_app_id: 'A1',
  event: { type: 'message', channel: 'D1', channel_type: 'im', ts: '1710000000.000001', user: 'U1', text: 'task' },
}

describe('durable admission ownership and reconciliation', () => {
  it('reconciles a lost provider response by client identity without a second post', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries.length = 0
    const dispatchRef = 'slack-admission-nudge:stable'
    const delivery = {
      id: 'nudge-1',
      conversationId: 'D1',
      threadTs: null,
      payloadJson: JSON.stringify({ operation: 'post_message', text: 'setup', clientMessageId: dispatchRef }),
    }
    transport.deliveries.push(delivery)
    const web = new FakeWeb()
    const socket = new FakeSocket()
    web.throwAfterPost = true
    web.nextMessageTs = '1710000000.000010'
    web.historyAvailable = true
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

    expect(web.posted).toEqual([{ channel: 'D1', text: 'setup', client_msg_id: dispatchRef }])
    expect(transport.acks).toEqual([
      {
        ref: { projectId: 'p', connectionId: 'c' },
        id: 'nudge-1',
        outcome: 'uncertain',
      },
    ])

    web.throwAfterPost = false
    transport.uncertainDeliveries.push(delivery)
    await socket.emit(event)

    expect(web.posted).toHaveLength(1)
    expect(transport.acks.at(-1)).toEqual({
      ref: { projectId: 'p', connectionId: 'c' },
      id: 'nudge-1',
      outcome: 'delivered',
      providerMessageIdentity: { conversationId: 'D1', messageTs: '1710000000.000010' },
    })
    controller.abort()
    await adapter.stop()
  })

  it('retries the original durable delivery after reconciliation confirms absence', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries.length = 0
    const delivery = {
      id: 'nudge-2',
      conversationId: 'D1',
      threadTs: '1710000000.000000',
      payloadJson: JSON.stringify({ operation: 'post_message', text: 'setup', clientMessageId: 'stable-id' }),
    }
    transport.uncertainDeliveries.push(delivery)
    transport.retryDelivery = delivery
    const web = new FakeWeb()
    web.historyAvailable = true
    const adapter = new SlackAdapter({
      adapterId: 'adapter-1',
      transport,
      socketFactory: () => new FakeSocket(),
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await adapter.start(controller.signal)

    expect(web.posted).toEqual([
      { channel: 'D1', text: 'setup', thread_ts: '1710000000.000000', client_msg_id: 'stable-id' },
    ])
    expect(transport.acks).toEqual([
      { ref: { projectId: 'p', connectionId: 'c' }, id: 'nudge-2', outcome: 'retry' },
      { ref: { projectId: 'p', connectionId: 'c' }, id: 'nudge-2', outcome: 'delivered' },
    ])
    controller.abort()
    await adapter.stop()
  })
})
