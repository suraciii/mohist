import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SlackAdapter, normalizeSlackInteraction, normalizeSocketEvent } from './adapter.js'
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

  it('normalizes the Socket Mode interactive payload without copying its raw body', () => {
    const interaction = normalizeSlackInteraction({
      type: 'interactive',
      payload: JSON.stringify({
        type: 'block_actions',
        api_app_id: 'A1',
        trigger_id: 'trigger-1',
        team: { id: 'T1' },
        user: { id: 'U1' },
        container: { channel_id: 'C1', message_ts: '123.456', thread_ts: '123.000' },
        actions: [{ action_id: 'mohist_stop_turn', action_ts: '123.500', value: 'server-signed-value' }],
        token: 'xoxb-secret',
      }),
    })

    expect(interaction).toEqual({
      eventType: 'block_actions',
      apiAppId: 'A1',
      interactionId: 'trigger-1',
      teamId: 'T1',
      conversationId: 'C1',
      messageTs: '123.456',
      threadTs: '123.000',
      actorSlackUserId: 'U1',
      actionId: 'mohist_stop_turn',
      actionValue: 'server-signed-value',
    })
    expect(JSON.stringify(interaction)).not.toContain('xoxb-secret')
  })

  it('acknowledges an interaction before waiting for Server processing', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries.length = 0
    let releaseInteraction!: () => void
    let markInteractionStarted!: () => void
    transport.interactionGate = new Promise<void>((resolve) => {
      releaseInteraction = resolve
    })
    const interactionStarted = new Promise<void>((resolve) => {
      markInteractionStarted = resolve
    })
    transport.interactionStarted = markInteractionStarted
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => socket,
      webFactory: () => new FakeWeb(),
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)
    const pending = socket.emit({
      type: 'block_actions',
      api_app_id: 'A1',
      trigger_id: 'trigger-1',
      team: { id: 'T1' },
      user: { id: 'U1' },
      container: { channel_id: 'C1', message_ts: '123.456' },
      actions: [{ action_id: 'mohist_stop_turn', action_ts: '123.500', value: 'signed-value' }],
    })

    await interactionStarted
    expect(transport.interactions).toHaveLength(1)
    expect(socket.acknowledged).toBe(true)
    releaseInteraction()
    await expect(pending).resolves.toBe(true)
    controller.abort()
  })

  it('contains a failed acknowledged interaction without crashing the socket callback', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries.length = 0
    transport.interactionError = new Error('Server returned 500 with xoxb-secret')
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => socket,
      webFactory: () => new FakeWeb(),
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)
    const body = {
      type: 'block_actions',
      api_app_id: 'A1',
      trigger_id: 'trigger-1',
      team: { id: 'T1' },
      user: { id: 'U1' },
      container: { channel_id: 'C1', message_ts: '123.456' },
      actions: [{ action_id: 'mohist_stop_turn', action_ts: '123.500', value: 'signed-value' }],
    }

    await expect(socket.emit(body)).resolves.toBe(true)
    expect(logger.entries).toContainEqual({
      level: 'error',
      message: 'interaction processing failed after acknowledgement',
      fields: {
        target: 'connection:p:c',
        event: 'block_actions',
        reason: 'Server returned 500 with <redacted>',
      },
    })

    transport.interactionError = undefined
    await expect(socket.emit(body)).resolves.toBe(true)
    controller.abort()
  })

  it('leaves a failed message event unacknowledged for Slack retry', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries.length = 0
    transport.ingressError = new Error('Server returned 500')
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => socket,
      webFactory: () => new FakeWeb(),
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)

    await expect(
      socket.emit({
        team_id: 'T1',
        api_app_id: 'A1',
        event: { type: 'message', channel: 'D1', channel_type: 'im', ts: '123.456', user: 'U1', text: 'do work' },
      }),
    ).resolves.toBe(false)
    expect(logger.entries).toContainEqual({
      level: 'error',
      message: 'event handling failed before acknowledgement',
      fields: {
        target: 'connection:p:c',
        event: 'message',
        reason: 'Server returned 500',
      },
    })
    controller.abort()
  })

  it('normalizes a Socket Mode event with stable identity', () => {
    expect(
      normalizeSocketEvent({
        team_id: 'T1',
        api_app_id: 'A1',
        event: { type: 'message', channel: 'D1', channel_type: 'im', ts: '123.456', user: 'U1', text: 'do work' },
      }),
    ).toEqual({
      eventType: 'message',
      apiAppId: 'A1',
      isDirectMessage: true,
      teamId: 'T1',
      conversationId: 'D1',
      messageTs: '123.456',
      threadTs: null,
      mentionedUserIds: [],
      senderSlackUserId: 'U1',
      senderKind: 'human',
      authorBot: null,
      text: 'do work',
      files: [],
    })
  })

  it('forwards file metadata without Slack secrets or raw payload', () => {
    const envelope = normalizeSocketEvent({
      team_id: 'T1',
      api_app_id: 'A1',
      bot_token: 'xoxb-secret',
      event: {
        type: 'message',
        subtype: 'file_share',
        channel: 'D1',
        ts: '123.456',
        user: 'U1',
        text: 'read these',
        files: [
          {
            id: 'F1',
            name: 'report.txt',
            mimetype: 'text/plain',
            size: 42,
            url_private: 'https://files.slack.com/secret',
            url_private_download: 'https://files.slack.com/download-secret',
          },
          {
            id: 'F2',
            name: 'image.png',
            mimetype: 'image/png',
            size: 2048,
            permalink: 'https://workspace.slack.com/files/F2',
          },
        ],
      },
    })

    expect(envelope.files).toEqual([
      { id: 'F1', name: 'report.txt', mimetype: 'text/plain', size: 42 },
      { id: 'F2', name: 'image.png', mimetype: 'image/png', size: 2048 },
    ])
    expect(JSON.stringify(envelope)).not.toContain('url_private')
    expect(JSON.stringify(envelope)).not.toContain('xoxb-secret')
    expect(envelope).not.toHaveProperty('event')
  })

  it('normalizes channel threads, all mentions, bot senders, and unknown senders', () => {
    expect(
      normalizeSocketEvent({
        team_id: 'T1',
        api_app_id: 'A1',
        event: {
          type: 'message',
          channel: 'C1',
          ts: '123.456',
          thread_ts: '123.000',
          user: 'U1',
          text: '<@B1> ask <@B2|other> and <@B1> again',
        },
      }),
    ).toMatchObject({
      threadTs: '123.000',
      mentionedUserIds: ['B1', 'B2'],
      senderSlackUserId: 'U1',
      senderKind: 'human',
    })

    expect(
      normalizeSocketEvent({
        team_id: 'T1',
        api_app_id: 'A1',
        event: { channel: 'C1', ts: '123.457', subtype: 'bot_message', bot_id: 'B1', text: 'reply' },
      }),
    ).toMatchObject({
      teamId: 'T1',
      conversationId: 'C1',
      messageTs: '123.457',
      senderSlackUserId: null,
      senderKind: 'bot',
      authorBot: { appId: null, botId: 'B1', botUserId: null, identityConflict: false },
    })

    expect(
      normalizeSocketEvent({
        team_id: 'T1',
        api_app_id: 'A1',
        event: { channel: 'C1', ts: '123.458', text: 'system event' },
      }),
    ).toMatchObject({
      teamId: 'T1',
      conversationId: 'C1',
      messageTs: '123.458',
      senderSlackUserId: null,
      senderKind: 'unknown',
    })
  })

  it('normalizes supported Manager and Agent Bot author fixtures without using the receiver identity', () => {
    const manager = normalizeSocketEvent({
      team_id: 'T_MANAGER',
      api_app_id: 'A_MANAGER_RECEIVER',
      event: {
        type: 'message',
        subtype: 'bot_message',
        channel: 'D_MANAGER',
        ts: '200.001',
        bot_profile: { app_id: 'A_MANAGER_AUTHOR', id: 'B_MANAGER_AUTHOR', name: 'Mohist Manager' },
        text: 'manager response',
      },
    })
    const agent = normalizeSocketEvent({
      team_id: 'T_AGENT',
      api_app_id: 'A_MANAGER_RECEIVER',
      event: {
        type: 'message',
        subtype: 'bot_message',
        channel: 'D_AGENT',
        ts: '200.002',
        app_id: 'A_AGENT_AUTHOR',
        bot_id: 'B_AGENT_AUTHOR',
        bot_profile: { id: 'B_AGENT_AUTHOR' },
        user: 'U_AGENT_AUTHOR',
        text: 'agent response',
      },
    })

    expect(manager).toMatchObject({
      apiAppId: 'A_MANAGER_RECEIVER',
      senderKind: 'bot',
      senderSlackUserId: null,
      authorBot: {
        appId: 'A_MANAGER_AUTHOR',
        botId: 'B_MANAGER_AUTHOR',
        botUserId: null,
        identityConflict: false,
      },
    })
    expect(agent).toMatchObject({
      apiAppId: 'A_MANAGER_RECEIVER',
      senderKind: 'bot',
      senderSlackUserId: null,
      authorBot: {
        appId: 'A_AGENT_AUTHOR',
        botId: 'B_AGENT_AUTHOR',
        botUserId: 'U_AGENT_AUTHOR',
        identityConflict: false,
      },
    })
    expect(requireSupportedMohistAuthorAppId(manager, 'Manager')).toBe('A_MANAGER_AUTHOR')
    expect(requireSupportedMohistAuthorAppId(agent, 'Agent')).toBe('A_AGENT_AUTHOR')
    expect(manager.apiAppId).not.toBe(manager.authorBot?.appId)
    expect(agent.apiAppId).not.toBe(agent.authorBot?.appId)
    expect(JSON.stringify(manager)).not.toContain('bot_profile')
    expect(JSON.stringify(manager)).not.toContain('Mohist Manager')
  })

  it('fails supported Mohist fixture contracts when both App-ID sources are absent', () => {
    const manager = normalizeSocketEvent({
      team_id: 'T_MANAGER',
      api_app_id: 'A_MANAGER_RECEIVER',
      event: {
        type: 'message',
        subtype: 'bot_message',
        channel: 'D_MANAGER',
        ts: '200.003',
        bot_profile: { id: 'B_MANAGER_AUTHOR' },
        text: 'manager response',
      },
    })
    const agent = normalizeSocketEvent({
      team_id: 'T_AGENT',
      api_app_id: 'A_MANAGER_RECEIVER',
      event: {
        type: 'message',
        subtype: 'bot_message',
        channel: 'D_AGENT',
        ts: '200.004',
        bot_id: 'B_AGENT_AUTHOR',
        bot_profile: { id: 'B_AGENT_AUTHOR' },
        text: 'agent response',
      },
    })

    expect(() => requireSupportedMohistAuthorAppId(manager, 'Manager')).toThrow(
      'Manager fixture is missing a matchable author App-ID',
    )
    expect(() => requireSupportedMohistAuthorAppId(agent, 'Agent')).toThrow(
      'Agent fixture is missing a matchable author App-ID',
    )
  })

  it('marks conflicting Bot author fields instead of selecting an unsafe identity', () => {
    const envelope = normalizeSocketEvent({
      team_id: 'T1',
      api_app_id: 'A_RECEIVER',
      event: {
        type: 'message',
        subtype: 'bot_message',
        channel: 'C1',
        ts: '201.001',
        app_id: 'A_EVENT_AUTHOR',
        bot_id: 'B_EVENT_AUTHOR',
        bot_profile: { app_id: 'A_PROFILE_AUTHOR', id: 'B_PROFILE_AUTHOR' },
        user: 'U_AUTHOR',
        text: 'conflicting response',
      },
    })

    expect(envelope).toMatchObject({
      apiAppId: 'A_RECEIVER',
      senderKind: 'bot',
      senderSlackUserId: null,
      authorBot: {
        appId: 'A_EVENT_AUTHOR',
        botId: 'B_EVENT_AUTHOR',
        botUserId: 'U_AUTHOR',
        identityConflict: true,
      },
    })
  })

  it('does not invent a Bot App identity or leak raw Slack fields', () => {
    const envelope = normalizeSocketEvent({
      team_id: 'T1',
      api_app_id: 'A_RECEIVER',
      token: 'xoxb-secret',
      event: {
        type: 'message',
        subtype: 'bot_message',
        channel: 'C1',
        ts: '201.002',
        bot_id: 'B_AUTHOR',
        bot_profile: { id: 'B_AUTHOR', app_name: 'hidden app name' },
        text: 'bot response',
        client_msg_id: 'raw-message-id',
      },
    })

    expect(envelope.senderKind).toBe('bot')
    expect(envelope.senderSlackUserId).toBeNull()
    expect(envelope.authorBot).toEqual({
      appId: null,
      botId: 'B_AUTHOR',
      botUserId: null,
      identityConflict: false,
    })
    expect(JSON.stringify(envelope)).not.toContain('xoxb-secret')
    expect(JSON.stringify(envelope)).not.toContain('bot_profile')
    expect(JSON.stringify(envelope)).not.toContain('raw-message-id')
    expect(JSON.stringify(envelope)).not.toContain('hidden app name')
  })

  it('keeps an App-less Bot distinct from a supported fixture contract', () => {
    const envelope = normalizeSocketEvent({
      team_id: 'T1',
      api_app_id: 'A_RECEIVER',
      event: { channel: 'C1', ts: '201.003', subtype: 'bot_message', bot_id: 'B_AUTHOR', text: 'bot response' },
    })

    expect(envelope.senderKind).toBe('bot')
    expect(envelope.authorBot).toMatchObject({ appId: null, botId: 'B_AUTHOR' })
    expect(envelope.authorBot?.appId).not.toBe(envelope.apiAppId)
  })

  it('acknowledges an ignored result once without rejecting or stopping normal delivery draining', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries.length = 0
    transport.nextIngressResults = [{ kind: 'ignored' }]
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
    transport.deliveries.push({
      id: 'existing-delivery',
      conversationId: 'C1',
      threadTs: null,
      payloadJson: JSON.stringify({ text: 'existing response' }),
    })

    await expect(
      socket.emit({
        team_id: 'T1',
        api_app_id: 'A1',
        event: { channel: 'C1', ts: '202.001', subtype: 'bot_message', bot_id: 'B1', text: 'ignored response' },
      }),
    ).resolves.toBe(true)

    expect(socket.acknowledgementCount).toBe(1)
    expect(web.posted).toEqual([{ channel: 'C1', text: 'existing response' }])
    expect(web.updated).toEqual([])
    expect(web.uploaded).toEqual([])
    expect(transport.acks).toEqual([{ ref: { projectId: 'p', connectionId: 'c' }, id: 'existing-delivery', outcome: 'delivered' }])
    await adapter.stop()
  })

  it('acknowledges bot and unknown events without requiring a user id', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries.length = 0
    const socket = new FakeSocket()
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => socket,
      webFactory: () => new FakeWeb(),
      heartbeatIntervalMs: 60_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)

    await expect(
      socket.emit({
        team_id: 'T1',
        api_app_id: 'A1',
        event: { channel: 'C1', ts: '123.457', subtype: 'bot_message', bot_id: 'B1', text: 'reply' },
      }),
    ).resolves.toBe(true)
    await expect(
      socket.emit({
        team_id: 'T1',
        api_app_id: 'A1',
        event: { channel: 'C1', ts: '123.458', text: 'system event' },
      }),
    ).resolves.toBe(true)
    expect(transport.envelopes.map((envelope) => [envelope.messageTs, envelope.senderKind])).toEqual([
      ['123.457', 'bot'],
      ['123.458', 'unknown'],
    ])
    controller.abort()
  })
})

function requireSupportedMohistAuthorAppId(
  envelope: ReturnType<typeof normalizeSocketEvent>,
  fixtureName: string,
): string {
  const appId = envelope.authorBot?.appId
  if (envelope.senderKind !== 'bot' || !appId || envelope.authorBot?.identityConflict)
    throw new Error(`${fixtureName} fixture is missing a matchable author App-ID`)
  return appId
}
