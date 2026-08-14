import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SlackAdapter } from './adapter.js'
import { setSlackLoggerForTest } from './logger.js'
import { FakeSocket, FakeTransport, FakeWeb, RecordingLogger, runtimeLease } from './_adapterTestSupport.js'

describe('mohist-slack adapter', () => {
  let logger: RecordingLogger
  let restoreLogger: () => void

  beforeEach(() => {
    logger = new RecordingLogger()
    restoreLogger = setSlackLoggerForTest(logger)
  })

  afterEach(() => {
    vi.useRealTimers()
    restoreLogger()
  })

  it('discovers connections, forwards every event to ingress, and drains replies', async () => {
    const transport = new FakeTransport()
    transport.connections = [
      { projectId: 'p1', connectionId: 'c1' },
      { projectId: 'p1', connectionId: 'c2' },
    ]
    const sockets = new Map<string, FakeSocket>()
    const webs = new Map<string, FakeWeb>()
    const adapter = new SlackAdapter({
      adapterId: 'adapter-1',
      transport,
      socketFactory: (_token, ref) => {
        const socket = new FakeSocket()
        sockets.set(ref.connectionId, socket)
        return socket
      },
      webFactory: (_token, ref) => {
        const web = new FakeWeb()
        webs.set(ref.connectionId, web)
        return web
      },
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })

    const controller = new AbortController()
    await adapter.start(controller.signal)
    expect(transport.leases.map((ref) => ref.connectionId)).toEqual(['c1', 'c2'])
    expect(sockets.get('c1')?.started).toBe(true)
    expect(
      await sockets.get('c1')?.emit({
        team_id: 'T1',
        api_app_id: 'A1',
        event: { type: 'message', channel: 'D1', channel_type: 'im', ts: '1.2', user: 'U1', text: 'task' },
      }),
    ).toBe(true)
    expect(transport.envelopes).toHaveLength(1)
    expect(transport.envelopes[0]?.text).toBe('task')
    expect(logger.entries).toContainEqual({
      level: 'info',
      message: 'envelope received',
      fields: { target: 'connection:p1:c1', event: 'message' },
    })
    expect(logger.entries).toContainEqual({
      level: 'info',
      message: 'envelope forwarding',
      fields: { target: 'connection:p1:c1', event: 'message' },
    })
    expect(logger.entries).toContainEqual({
      level: 'info',
      message: 'ingress accepted',
      fields: { target: 'connection:p1:c1', event: 'message', kind: 'accepted' },
    })
    expect(webs.get('c1')?.posted).toEqual([{ channel: 'D1', text: 'accepted' }])
    expect(transport.acks).toEqual([
      { ref: { projectId: 'p1', connectionId: 'c1' }, id: 'delivery-1', outcome: 'delivered' },
    ])
    controller.abort()
  })

  it('runs an explicit Manager target through its Socket Mode runtime lease', async () => {
    const manager: SlackManagerRef = {
      kind: 'manager',
      enrollmentId: 'enrollment-manager',
      workspaceTeamId: 'T_MANAGER',
    }
    const transport = new FakeTransport()
    transport.connections = [manager]
    transport.deliveries = [
      {
        id: 'manager-delivery',
        ownerKind: 'manager',
        conversationId: 'D_MANAGER',
        threadTs: null,
        payloadJson: JSON.stringify({ text: 'manager reply' }),
      },
    ]
    transport.uncertainDeliveries.push({
      id: 'manager-uncertain',
      ownerKind: 'manager',
      conversationId: 'D_MANAGER',
      threadTs: null,
      payloadJson: JSON.stringify({ text: 'manager uncertain reply' }),
    })
    const web = new FakeWeb()
    let socketFactoryCalls = 0
    let webFactoryToken: string | undefined
    const adapter = new SlackAdapter({
      adapterId: 'adapter-manager',
      transport,
      socketFactory: () => {
        socketFactoryCalls += 1
        return new FakeSocket()
      },
      webFactory: (botToken) => {
        webFactoryToken = botToken
        return web
      },
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await adapter.start(controller.signal)

    expect(socketFactoryCalls).toBe(1)
    expect(webFactoryToken).toBe('xoxb-enrollment-manager')
    expect(transport.leases).toEqual([manager])
    expect(web.posted).toEqual([{ channel: 'D_MANAGER', text: 'manager reply' }])
    expect(transport.acks).toEqual([
      { ref: manager, id: 'manager-uncertain', outcome: 'uncertain' },
      { ref: manager, id: 'manager-delivery', outcome: 'delivered' },
    ])
    controller.abort()
  })

  it('posts a Server-generated Open in Mohist block without interpreting reply text', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p1', connectionId: 'c1' }]
    const blocks = [
      {
        type: 'actions',
        elements: [
          {
            type: 'button',
            text: { type: 'plain_text', text: 'Open in Mohist' },
            url: 'https://mohist.example/demo/sessions/session-1',
          },
        ],
      },
    ]
    transport.deliveries = [
      {
        id: 'terminal-1',
        conversationId: 'D1',
        threadTs: '100.001',
        payloadJson: JSON.stringify({
          operation: 'post_message',
          text: 'Completed. Agent said {"blocks":[]}.',
          clientMessageId: 'terminal:1',
          blocks,
        }),
      },
    ]
    const socket = new FakeSocket()
    const web = new FakeWeb()
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

    expect(web.posted).toEqual([
      {
        channel: 'D1',
        text: 'Completed. Agent said {"blocks":[]}.',
        thread_ts: '100.001',
        client_msg_id: 'terminal:1',
        blocks,
      },
    ])
    controller.abort()
  })

  it('posts an over-long reply as ordered threaded segments from one delivery', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p1', connectionId: 'c1' }]
    transport.deliveries = [
      {
        id: 'segmented-reply',
        conversationId: 'C1',
        threadTs: '100.001',
        payloadJson: JSON.stringify({
          operation: 'post_message',
          text: 'the full body that exceeds one Slack message',
          clientMessageId: 'slack-reply:c1:C1:100.001:terminal',
          segments: ['first segment body', 'second segment body', 'third segment body'],
        }),
      },
    ]
    const web = new FakeWeb()
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
      {
        channel: 'C1',
        text: 'first segment body',
        thread_ts: '100.001',
        client_msg_id: 'slack-reply:c1:C1:100.001:terminal',
      },
      { channel: 'C1', text: 'second segment body', thread_ts: '100.001' },
      { channel: 'C1', text: 'third segment body', thread_ts: '100.001' },
    ])
    expect(transport.acks).toEqual([
      { ref: { projectId: 'p1', connectionId: 'c1' }, id: 'segmented-reply', outcome: 'delivered' },
    ])
    controller.abort()
  })

  it('uploads a local image file as a Slack file share reply', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p1', connectionId: 'c1' }]
    transport.deliveries = [
      {
        id: 'delivery-file',
        conversationId: 'D1',
        threadTs: '1710000000.000100',
        payloadJson: JSON.stringify({
          operation: 'upload_file',
          text: 'screenshot attached',
          fileName: 'shot.png',
          fileContentBase64: Buffer.from('png-bytes').toString('base64'),
        }),
      },
      {
        id: 'delivery-file-dm',
        conversationId: 'D2',
        threadTs: null,
        payloadJson: JSON.stringify({
          operation: 'upload_file',
          fileName: 'shot.png',
          fileContentBase64: Buffer.from('png-bytes').toString('base64'),
        }),
      },
    ]
    const web = new FakeWeb()
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

    expect(web.uploaded).toHaveLength(2)
    expect(web.uploaded[0]).toMatchObject({
      channels: 'D1',
      thread_ts: '1710000000.000100',
      filename: 'shot.png',
      initial_comment: 'screenshot attached',
    })
    expect(web.uploaded[0]?.file).toEqual(Buffer.from('png-bytes'))
    expect(web.uploaded[1]).toMatchObject({ channel_id: 'D2', filename: 'shot.png' })
    expect(web.uploaded[1]?.initial_comment).toBeUndefined()
    expect(transport.acks).toEqual([
      { ref: { projectId: 'p1', connectionId: 'c1' }, id: 'delivery-file', outcome: 'delivered' },
      { ref: { projectId: 'p1', connectionId: 'c1' }, id: 'delivery-file-dm', outcome: 'delivered' },
    ])
    controller.abort()
  })

  it('posts an image-only reply with blocks and without a text body', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p1', connectionId: 'c1' }]
    const blocks = [{ type: 'image', image_url: 'https://example.com/p.png', alt_text: 'Image' }]
    transport.deliveries = [
      {
        id: 'delivery-image',
        conversationId: 'D1',
        threadTs: null,
        payloadJson: JSON.stringify({
          operation: 'post_message',
          clientMessageId: 'slack-reply:c1:D1:dm:image',
          blocks,
        }),
      },
    ]
    const web = new FakeWeb()
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
      {
        channel: 'D1',
        text: '',
        client_msg_id: 'slack-reply:c1:D1:dm:image',
        blocks,
      },
    ])
    expect(transport.acks).toEqual([
      { ref: { projectId: 'p1', connectionId: 'c1' }, id: 'delivery-image', outcome: 'delivered' },
    ])
    controller.abort()
  })

  it('records the file share identity from the upload response for reconciliation', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p1', connectionId: 'c1' }]
    transport.deliveries = [
      {
        id: 'delivery-file-identity',
        conversationId: 'C_PUBLIC',
        threadTs: null,
        payloadJson: JSON.stringify({
          operation: 'upload_file',
          fileName: 'shot.png',
          fileContentBase64: Buffer.from('png-bytes').toString('base64'),
        }),
      },
    ]
    const web = new FakeWeb()
    web.nextUploadResponses = [
      {
        ok: true,
        files: [{ ok: true, files: [{ id: 'F1', shares: { public: { C_PUBLIC: [{ ts: '1710000000.000200' }] } } }] }],
      },
    ]
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

    expect(transport.acks).toEqual([
      {
        ref: { projectId: 'p1', connectionId: 'c1' },
        id: 'delivery-file-identity',
        outcome: 'delivered',
        providerMessageIdentity: { conversationId: 'C_PUBLIC', messageTs: '1710000000.000200' },
      },
    ])
    controller.abort()
  })

  it('forwards interactions to the Server and drains its block update after acknowledging', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p1', connectionId: 'c1' }]
    transport.deliveries = [
      {
        id: 'control-update',
        conversationId: 'C1',
        threadTs: '100.001',
        payloadJson: JSON.stringify({
          operation: 'chat_update',
          text: 'Stop requested. Waiting for the runtime to confirm.',
          providerMessageIdentity: { conversationId: 'C1', messageTs: '100.002' },
          blocks: [
            { type: 'section', text: { type: 'mrkdwn', text: 'Stop requested. Waiting for the runtime to confirm.' } },
          ],
        }),
      },
    ]
    const socket = new FakeSocket()
    const web = new FakeWeb()
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

    const acknowledged = await socket.emit({
      type: 'interactive',
      payload: JSON.stringify({
        type: 'block_actions',
        api_app_id: 'A1',
        trigger_id: 'trigger-1',
        team: { id: 'T1' },
        user: { id: 'U1' },
        container: { channel_id: 'C1', message_ts: '100.002', thread_ts: '100.001' },
        actions: [{ action_id: 'mohist_stop_turn', value: 'server-signed-value' }],
      }),
    })

    expect(acknowledged).toBe(true)
    expect(transport.interactions).toEqual([
      {
        eventType: 'block_actions',
        apiAppId: 'A1',
        interactionId: 'trigger-1',
        teamId: 'T1',
        conversationId: 'C1',
        messageTs: '100.002',
        threadTs: '100.001',
        actorSlackUserId: 'U1',
        actionId: 'mohist_stop_turn',
        actionValue: 'server-signed-value',
      },
    ])
    expect(web.updated).toEqual([
      {
        channel: 'C1',
        ts: '100.002',
        text: 'Stop requested. Waiting for the runtime to confirm.',
        blocks: [
          { type: 'section', text: { type: 'mrkdwn', text: 'Stop requested. Waiting for the runtime to confirm.' } },
        ],
      },
    ])
    expect(transport.acks).toEqual([
      {
        ref: { projectId: 'p1', connectionId: 'c1' },
        id: 'control-update',
        outcome: 'delivered',
        providerMessageIdentity: { conversationId: 'C1', messageTs: '100.002' },
      },
    ])
    controller.abort()
  })

  it('starts with zero connections and reconciles later additions and removals', async () => {
    const transport = new FakeTransport()
    const sockets = new Map<string, FakeSocket>()
    const adapter = new SlackAdapter({
      adapterId: 'adapter-1',
      transport,
      socketFactory: (_token, ref) => {
        const socket = new FakeSocket()
        sockets.set(ref.connectionId, socket)
        return socket
      },
      webFactory: () => new FakeWeb(),
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await expect(adapter.start(controller.signal)).resolves.toBeUndefined()
    expect(transport.leases).toEqual([])

    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    await adapter.refreshConnections(controller.signal)
    expect(sockets.get('c')?.started).toBe(true)

    transport.connections = []
    await adapter.refreshConnections(controller.signal)
    expect(sockets.get('c')?.disconnected).toBe(true)
    controller.abort()
  })

  it('posts threaded deliveries in a thread and DM deliveries without a thread target', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries.push({
      id: 'delivery-2',
      conversationId: 'C1',
      threadTs: '1.2',
      payloadJson: JSON.stringify({ text: 'thread reply' }),
    })
    const web = new FakeWeb()
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => socket,
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await adapter.start(controller.signal)
    expect(web.posted).toEqual([
      { channel: 'D1', text: 'accepted' },
      { channel: 'C1', text: 'thread reply', thread_ts: '1.2' },
    ])
    controller.abort()
  })

  it('posts the backpressured reason to the originating conversation so the sender can see the refusal', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries.length = 0
    transport.nextIngressResults = [
      {
        kind: 'backpressured',
        reason: 'This Slack Connection is backpressured; retry after pending deliveries drain.',
      },
    ]
    const web = new FakeWeb()
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => socket,
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await adapter.start(controller.signal)
    const acknowledged = await socket.emit({
      team_id: 'T1',
      api_app_id: 'A1',
      event: {
        type: 'message',
        channel: 'D1',
        channel_type: 'im',
        ts: '123.456',
        thread_ts: '123.000',
        user: 'U1',
        text: 'do work',
      },
    })

    expect(acknowledged).toBe(true)
    expect(web.posted).toEqual([
      {
        channel: 'D1',
        text: 'This Slack Connection is backpressured; retry after pending deliveries drain.',
        thread_ts: '123.000',
      },
    ])
    expect(transport.acks).toEqual([])
    controller.abort()
  })

  it('does not render server-enqueued rejected kinds so the outbox reply is not duplicated', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries.length = 0
    transport.nextIngressResults = [{ kind: 'rejected', reason: 'Please send a task for the Agent to perform.' }]
    const web = new FakeWeb()
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => socket,
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await adapter.start(controller.signal)
    const acknowledged = await socket.emit({
      team_id: 'T1',
      api_app_id: 'A1',
      event: { type: 'message', channel: 'D1', channel_type: 'im', ts: '123.456', user: 'U1', text: '' },
    })

    expect(acknowledged).toBe(true)
    expect(web.posted).toEqual([])
    controller.abort()
  })

  it('distinguishes a backpressured refusal from an accepted result that is still pending', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries.length = 0
    transport.nextIngressResults = [{ kind: 'accepted' }, { kind: 'backpressured', reason: 'retry shortly' }]
    const web = new FakeWeb()
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => socket,
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()

    await adapter.start(controller.signal)
    const firstAck = await socket.emit({
      team_id: 'T1',
      api_app_id: 'A1',
      event: { type: 'message', channel: 'D1', channel_type: 'im', ts: '1710000000.000001', user: 'U1', text: 'first' },
    })
    const secondAck = await socket.emit({
      team_id: 'T1',
      api_app_id: 'A1',
      event: {
        type: 'message',
        channel: 'D1',
        channel_type: 'im',
        ts: '1710000000.000002',
        user: 'U1',
        text: 'second',
      },
    })

    expect(firstAck).toBe(true)
    expect(secondAck).toBe(true)
    expect(web.posted).toEqual([{ channel: 'D1', text: 'retry shortly' }])
    controller.abort()
  })

  it('acks explicit Slack rejections as retry so the same post can be re-sent without duplicating', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries = [
      { id: 'delivery-rejected', conversationId: 'D1', threadTs: null, payloadJson: JSON.stringify({ text: 'ok?' }) },
    ]
    transport.deliveries.push({
      id: 'delivery-accepted',
      conversationId: 'D1',
      threadTs: null,
      payloadJson: JSON.stringify({ text: 'ok?' }),
    })
    const web = new FakeWeb()
    web.nextResponses = [{ ok: false, error: 'channel_not_found' }, { ok: true }]
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => socket,
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)

    expect(web.posted).toHaveLength(2)
    expect(transport.acks).toEqual([
      { ref: { projectId: 'p', connectionId: 'c' }, id: 'delivery-rejected', outcome: 'retry' },
      { ref: { projectId: 'p', connectionId: 'c' }, id: 'delivery-accepted', outcome: 'delivered' },
    ])
    expect(web.posted).toEqual([
      { channel: 'D1', text: 'ok?' },
      { channel: 'D1', text: 'ok?' },
    ])
    controller.abort()
  })

  it('acks transport or payload errors as uncertain so the row is held for operator action', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries = [
      { id: 'delivery-bad-json', conversationId: 'D1', threadTs: null, payloadJson: 'not-json' },
      { id: 'delivery-no-text', conversationId: 'D1', threadTs: null, payloadJson: JSON.stringify({ text: null }) },
    ]
    const web = new FakeWeb()
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => socket,
      webFactory: () => web,
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)

    expect(transport.acks).toHaveLength(2)
    expect(transport.acks).toEqual([
      { ref: { projectId: 'p', connectionId: 'c' }, id: 'delivery-bad-json', outcome: 'uncertain' },
      { ref: { projectId: 'p', connectionId: 'c' }, id: 'delivery-no-text', outcome: 'uncertain' },
    ])
    expect(web.posted).toEqual([])
    controller.abort()
  })

  it('limits concurrent ingress without using a durable queue', async () => {
    const socket = new FakeSocket()
    let active = 0
    let maximum = 0
    let releaseFirst!: () => void
    let markFirstStarted!: () => void
    const first = new Promise<void>((resolve) => {
      releaseFirst = resolve
    })
    const firstStarted = new Promise<void>((resolve) => {
      markFirstStarted = resolve
    })
    const transport: AdapterTransport = {
      discover: async () => [{ projectId: 'p', connectionId: 'c' }],
      acquireLease: async (_ref, kind) =>
        kind === 'validation'
          ? null
          : {
              kind: 'runtime',
              leaseId: 'lease',
              generation: 1,
              expiresAt: '2026-01-01T00:05:00Z',
              appToken: 'app',
              botToken: 'bot',
            },
      renewLease: async () => ({ leaseId: 'lease', kind: 'runtime', generation: 1, expiresAt: '2026-01-01T00:05:00Z' }),
      reportHello: async () => 'verified',
      ingress: async () => {
        active += 1
        maximum = Math.max(maximum, active)
        if (active === 1) {
          markFirstStarted()
          await first
        }
        active -= 1
        return { kind: 'accepted' }
      },
      interaction: async () => ({ state: 'stop_requested' }),
      claimDelivery: async () => null,
      ackDelivery: async () => undefined,
    }
    vi.useFakeTimers()
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => socket,
      webFactory: () => new FakeWeb(),
      maxInFlight: 1,
      deliveryPollIntervalMs: 60_000,
      heartbeatIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)
    const event = { team_id: 'T', api_app_id: 'A1', event: { channel: 'D', ts: '1', user: 'U', text: 'x' } }
    const firstEvent = socket.emit(event)
    await firstStarted
    const secondEvent = socket.emit({ ...event, event: { ...event.event, ts: '2' } })
    await vi.advanceTimersByTimeAsync(10)
    expect(maximum).toBe(1)
    releaseFirst()
    await vi.advanceTimersByTimeAsync(5)
    await Promise.all([firstEvent, secondEvent])
    controller.abort()
  })
})
