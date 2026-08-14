import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SlackAdapter } from './adapter.js'
import { LeaseStaleError } from './transport.js'
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

  it('logs a failed target connection with its identity and redacted credential', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p1', connectionId: 'c1' }]
    transport.leaseError = new Error('Slack rejected xapp-secret-value')
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => new FakeSocket(),
      webFactory: () => new FakeWeb(),
    })

    await adapter.start(new AbortController().signal)

    expect(logger.entries).toContainEqual({
      level: 'error',
      message: 'target connection failed',
      fields: {
        target: 'connection:p1:c1',
        reason: 'Slack rejected <redacted>',
      },
    })
    expect(JSON.stringify(logger.entries)).not.toContain('xapp-secret-value')
    await adapter.stop()
  })

  it('contains and redacts a Socket disconnect failure', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p1', connectionId: 'c1' }]
    const socket = new FakeSocket()
    socket.disconnectError = new Error('disconnect rejected xapp-secret-value')
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => socket,
      webFactory: () => new FakeWeb(),
    })

    await adapter.start(new AbortController().signal)
    await adapter.stop()

    expect(logger.entries).toContainEqual({
      level: 'error',
      message: 'socket disconnect failed',
      fields: {
        target: 'connection:p1:c1',
        reason: 'disconnect rejected <redacted>',
      },
    })
    expect(JSON.stringify(logger.entries)).not.toContain('xapp-secret-value')
  })

  it('does not log an in-flight lease cancellation during shutdown', async () => {
    vi.useFakeTimers()
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p1', connectionId: 'c1' }]
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => new FakeSocket(),
      webFactory: () => new FakeWeb(),
      heartbeatIntervalMs: 1_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)
    let markLeaseStarted!: () => void
    let markLeaseSettled!: () => void
    const leaseStarted = new Promise<void>((resolve) => {
      markLeaseStarted = resolve
    })
    const leaseSettled = new Promise<void>((resolve) => {
      markLeaseSettled = resolve
    })
    vi.spyOn(transport, 'renewLease').mockImplementation(async (_ref, _leaseId, _adapterId, signal) => {
      markLeaseStarted()
      try {
        await new Promise<void>((_resolve, reject) =>
          signal.addEventListener('abort', () => reject(new DOMException('This operation was aborted', 'AbortError')), {
            once: true,
          }),
        )
      } finally {
        markLeaseSettled()
      }
      throw new Error('unreachable')
    })

    vi.advanceTimersByTime(1_000)
    await leaseStarted
    controller.abort()
    await leaseSettled
    await Promise.resolve()
    await adapter.stop()
    expect(logger.entries).not.toContainEqual(
      expect.objectContaining({
        level: 'error',
        message: 'target lease refresh failed',
      }),
    )
  })

  it('uses a validation lease for exactly one hello without creating a runtime', async () => {
    const transport = new FakeTransport()
    const ref = { projectId: 'p', connectionId: 'c' }
    transport.connections = [ref]
    transport.nextLeases = [
      {
        kind: 'validation',
        leaseId: 'validation-lease',
        generation: 1,
        expiresAt: '2026-01-01T00:02:00Z',
        expectedAppId: 'A1',
        appToken: 'xapp-candidate-secret',
      },
      null,
    ]
    const socket = new FakeSocket()
    let webFactoryCalls = 0
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => socket,
      webFactory: () => {
        webFactoryCalls += 1
        return new FakeWeb()
      },
      discoveryIntervalMs: 60_000,
    })

    await adapter.start(new AbortController().signal)

    expect(socket.starts).toBe(1)
    expect(socket.disconnected).toBe(true)
    expect(transport.hellos).toEqual([{ ref, leaseId: 'validation-lease', appId: 'A1' }])
    expect(
      await socket.emit({
        team_id: 'T',
        api_app_id: 'A1',
        event: { type: 'message', channel: 'D', ts: '1', user: 'U', text: 'ignored' },
      }),
    ).toBe(false)
    expect(transport.envelopes).toEqual([])
    expect(transport.acks).toEqual([])
    expect(webFactoryCalls).toBe(0)
    expect(JSON.stringify(logger.entries)).not.toContain('xapp-candidate-secret')
    await adapter.stop()
  })

  it('does not start a Socket or delivery worker when discovery has no lease', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.nextLeases = [null, null]
    let socketFactoryCalls = 0
    let webFactoryCalls = 0
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => {
        socketFactoryCalls += 1
        return new FakeSocket()
      },
      webFactory: () => {
        webFactoryCalls += 1
        return new FakeWeb()
      },
      discoveryIntervalMs: 60_000,
    })

    await adapter.start(new AbortController().signal)

    expect(socketFactoryCalls).toBe(0)
    expect(webFactoryCalls).toBe(0)
    expect(transport.deliveries).toHaveLength(1)
    await adapter.stop()
  })

  it('disconnects an expired runtime lease before any late Socket event reaches Server', async () => {
    vi.useFakeTimers()
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries.length = 0
    const socket = new FakeSocket()
    vi.spyOn(transport, 'renewLease').mockResolvedValue(null)
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => socket,
      webFactory: () => new FakeWeb(),
      heartbeatIntervalMs: 1_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)

    await vi.advanceTimersByTimeAsync(1_000)
    expect(socket.disconnected).toBe(true)
    expect(
      await socket.emit({
        team_id: 'T',
        api_app_id: 'A1',
        event: { type: 'message', channel: 'D', ts: '1', user: 'U', text: 'late' },
      }),
    ).toBe(false)
    expect(transport.envelopes).toEqual([])
    controller.abort()
    await adapter.stop()
  })

  it('keeps the runtime Socket when a renewal extends the lease', async () => {
    vi.useFakeTimers()
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries.length = 0
    transport.nextRenewals = [{ leaseId: 'lease-c', kind: 'runtime', generation: 2, expiresAt: '2026-01-01T00:10:00Z' }]
    const sockets: FakeSocket[] = []
    const socketTokens: string[] = []
    const webTokens: string[] = []
    const webs: FakeWeb[] = []
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: (token) => {
        socketTokens.push(token)
        const socket = new FakeSocket()
        sockets.push(socket)
        return socket
      },
      webFactory: (token) => {
        webTokens.push(token)
        const web = new FakeWeb()
        webs.push(web)
        return web
      },
      heartbeatIntervalMs: 1_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)

    transport.deliveries.push({
      id: 'extended-delivery',
      conversationId: 'D',
      threadTs: null,
      payloadJson: JSON.stringify({ text: 'extended' }),
    })
    await vi.advanceTimersByTimeAsync(1_000)

    expect(socketTokens).toEqual(['xapp-c'])
    expect(webTokens).toEqual(['xoxb-c'])
    expect(sockets).toHaveLength(1)
    expect(sockets[0]?.disconnected).toBe(false)
    expect(webs[0]?.posted).toEqual([{ channel: 'D', text: 'extended' }])
    expect(transport.acks).toEqual([
      { ref: { projectId: 'p', connectionId: 'c' }, id: 'extended-delivery', outcome: 'delivered' },
    ])
    expect(logger.entries.some((entry) => entry.message === 'target lease refresh failed')).toBe(false)
    expect(JSON.stringify(logger.entries)).not.toContain('xapp-')
    expect(JSON.stringify(logger.entries)).not.toContain('xoxb-')
    controller.abort()
    await adapter.stop()
  })

  it('fences the old runtime while a foreign renewal waits for its Socket to disconnect', async () => {
    vi.useFakeTimers()
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries.length = 0
    transport.nextRenewals = [
      { leaseId: 'lease-foreign', kind: 'runtime', generation: 1, expiresAt: '2026-01-01T00:05:00Z' },
    ]
    const sockets: FakeSocket[] = []
    const webs: FakeWeb[] = []
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => {
        const socket = new FakeSocket()
        sockets.push(socket)
        return socket
      },
      webFactory: () => {
        const web = new FakeWeb()
        webs.push(web)
        return web
      },
      heartbeatIntervalMs: 1_000,
      deliveryPollIntervalMs: 100,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)
    let releaseDisconnect!: () => void
    let markDisconnectStarted!: () => void
    const disconnectGate = new Promise<void>((resolve) => {
      releaseDisconnect = resolve
    })
    const disconnectStarted = new Promise<void>((resolve) => {
      markDisconnectStarted = resolve
    })
    sockets[0]!.disconnectGate = disconnectGate
    sockets[0]!.disconnectStarted = markDisconnectStarted

    await vi.advanceTimersByTimeAsync(1_000)
    await disconnectStarted
    const claimsWhileDisconnected = transport.claimDeliveryCalls
    transport.deliveries.push({
      id: 'foreign-delivery',
      conversationId: 'D',
      threadTs: null,
      payloadJson: JSON.stringify({ text: 'pending' }),
    })
    await vi.advanceTimersByTimeAsync(100)

    expect(transport.claimDeliveryCalls).toBe(claimsWhileDisconnected)
    expect(webs[0]?.posted).toEqual([])
    expect(transport.acks).toEqual([])

    transport.deliveries.length = 0
    releaseDisconnect()
    await vi.advanceTimersByTimeAsync(0)
    expect(sockets[0]?.disconnected).toBe(true)
    expect(
      await sockets[0]!.emit({
        team_id: 'T',
        api_app_id: 'A1',
        event: { type: 'message', channel: 'D', ts: '1', user: 'U', text: 'late' },
      }),
    ).toBe(false)
    expect(transport.envelopes).toEqual([])
    controller.abort()
    await adapter.stop()
  })

  it('re-acquires a rotated runtime even when the old Socket disconnect fails', async () => {
    vi.useFakeTimers()
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries.length = 0
    transport.nextRenewals = [
      { leaseId: 'lease-foreign', kind: 'runtime', generation: 1, expiresAt: '2026-01-01T00:05:00Z' },
    ]
    const sockets: FakeSocket[] = []
    const webs: FakeWeb[] = []
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => {
        const socket = new FakeSocket()
        sockets.push(socket)
        return socket
      },
      webFactory: () => {
        const web = new FakeWeb()
        webs.push(web)
        return web
      },
      heartbeatIntervalMs: 1_000,
      deliveryPollIntervalMs: 100,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)
    let releaseDisconnect!: () => void
    let markDisconnectStarted!: () => void
    const disconnectGate = new Promise<void>((resolve) => {
      releaseDisconnect = resolve
    })
    const disconnectStarted = new Promise<void>((resolve) => {
      markDisconnectStarted = resolve
    })
    sockets[0]!.disconnectGate = disconnectGate
    sockets[0]!.disconnectStarted = markDisconnectStarted
    sockets[0]!.disconnectError = new Error('disconnect rejected xapp-secret-value')

    await vi.advanceTimersByTimeAsync(1_000)
    await disconnectStarted
    await vi.advanceTimersByTimeAsync(1_000)

    expect(sockets).toHaveLength(1)
    releaseDisconnect()
    await vi.advanceTimersByTimeAsync(0)

    transport.nextLeases = [
      null,
      {
        kind: 'runtime',
        leaseId: 'lease-rotated',
        generation: 1,
        expiresAt: '2026-01-01T00:05:00Z',
        appToken: 'xapp-rotated',
        botToken: 'xoxb-rotated',
      },
    ]
    await adapter.refreshConnections(controller.signal)
    expect(sockets).toHaveLength(2)
    expect(sockets[1]?.starts).toBe(1)

    transport.deliveries.push({
      id: 'current-delivery',
      conversationId: 'D',
      threadTs: null,
      payloadJson: JSON.stringify({ text: 'current' }),
    })
    await vi.advanceTimersByTimeAsync(100)

    expect(sockets[1]?.disconnected).toBe(false)
    expect(webs[1]?.posted).toEqual([{ channel: 'D', text: 'current' }])
    expect(transport.acks).toEqual([
      { ref: { projectId: 'p', connectionId: 'c' }, id: 'current-delivery', outcome: 'delivered' },
    ])
    expect(logger.entries).toContainEqual({
      level: 'error',
      message: 'socket disconnect failed',
      fields: { target: 'connection:p:c', reason: 'disconnect rejected <redacted>' },
    })
    expect(logger.entries.some((entry) => entry.message === 'target lease refresh failed')).toBe(false)
    expect(JSON.stringify(logger.entries)).not.toContain('xapp-secret-value')
    controller.abort()
    await adapter.stop()
  })

  it('reopens a runtime Socket with rotated credentials after a superseded lease is re-acquired', async () => {
    vi.useFakeTimers()
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries.length = 0
    transport.nextRenewals = [
      { leaseId: 'lease-foreign', kind: 'runtime', generation: 1, expiresAt: '2026-01-01T00:05:00Z' },
    ]
    const sockets: FakeSocket[] = []
    const socketTokens: string[] = []
    const webTokens: string[] = []
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: (token) => {
        socketTokens.push(token)
        const socket = new FakeSocket()
        sockets.push(socket)
        return socket
      },
      webFactory: (token) => {
        webTokens.push(token)
        return new FakeWeb()
      },
      heartbeatIntervalMs: 1_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)

    await vi.advanceTimersByTimeAsync(1_000)
    expect(sockets[0]?.disconnected).toBe(true)

    transport.nextLeases = [
      null,
      {
        kind: 'runtime',
        leaseId: 'lease-rotated',
        generation: 1,
        expiresAt: '2026-01-01T00:05:00Z',
        appToken: 'xapp-rotated',
        botToken: 'xoxb-rotated',
      },
    ]
    await adapter.refreshConnections(controller.signal)

    expect(socketTokens).toEqual(['xapp-c', 'xapp-rotated'])
    expect(webTokens).toEqual(['xoxb-c', 'xoxb-rotated'])
    expect(sockets[0]?.disconnected).toBe(true)
    expect(sockets[1]?.starts).toBe(1)
    expect(JSON.stringify(logger.entries)).not.toContain('xapp-rotated')
    expect(JSON.stringify(logger.entries)).not.toContain('xoxb-rotated')
    controller.abort()
    await adapter.stop()
  })

  it('fences a stale renewal that resolves after the runtime was removed', async () => {
    vi.useFakeTimers()
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries.length = 0
    const sockets: FakeSocket[] = []
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => {
        const socket = new FakeSocket()
        sockets.push(socket)
        return socket
      },
      webFactory: () => new FakeWeb(),
      heartbeatIntervalMs: 1_000,
      deliveryPollIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)
    const claimsBefore = transport.claimDeliveryCalls
    let releaseRenew!: () => void
    let markRenewStarted!: () => void
    const renewGate = new Promise<void>((resolve) => {
      releaseRenew = resolve
    })
    const renewStarted = new Promise<void>((resolve) => {
      markRenewStarted = resolve
    })
    vi.spyOn(transport, 'renewLease').mockImplementation(async (ref, leaseId) => {
      markRenewStarted()
      await renewGate
      return { leaseId, kind: 'runtime', generation: 1, expiresAt: '2026-01-01T00:05:00Z' }
    })

    await vi.advanceTimersByTimeAsync(1_000)
    await renewStarted
    transport.connections = []
    await adapter.refreshConnections(controller.signal)
    expect(sockets[0]?.disconnected).toBe(true)

    releaseRenew()
    await vi.advanceTimersByTimeAsync(0)
    expect(sockets).toHaveLength(1)
    expect(transport.claimDeliveryCalls).toBe(claimsBefore)
    expect(logger.entries.some((entry) => entry.message === 'target lease refresh failed')).toBe(false)
    controller.abort()
    await adapter.stop()
  })

  it('does not forward a Socket event that was waiting when a foreign renewal removes the runtime', async () => {
    vi.useFakeTimers()
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries.length = 0
    transport.nextRenewals = [
      { leaseId: 'lease-foreign', kind: 'runtime', generation: 1, expiresAt: '2026-01-01T00:05:00Z' },
    ]
    let releaseIngress!: () => void
    let markIngressStarted!: () => void
    transport.ingressGate = new Promise<void>((resolve) => {
      releaseIngress = resolve
    })
    const ingressStarted = new Promise<void>((resolve) => {
      markIngressStarted = resolve
    })
    transport.ingressStarted = markIngressStarted
    const sockets: FakeSocket[] = []
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => {
        const socket = new FakeSocket()
        sockets.push(socket)
        return socket
      },
      webFactory: () => new FakeWeb(),
      heartbeatIntervalMs: 1_000,
      deliveryPollIntervalMs: 60_000,
      maxInFlight: 1,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)

    const first = sockets[0]!.emit({
      team_id: 'T',
      api_app_id: 'A1',
      event: { type: 'message', channel: 'D', ts: '1', user: 'U', text: 'first' },
    })
    await ingressStarted
    const old = sockets[0]!.emit({
      team_id: 'T',
      api_app_id: 'A1',
      event: { type: 'message', channel: 'D', ts: '2', user: 'U', text: 'old' },
    })
    await vi.advanceTimersByTimeAsync(1_000)
    releaseIngress()
    await first
    await vi.advanceTimersByTimeAsync(5)
    await old

    expect(transport.envelopes.map((envelope) => envelope.messageTs)).toEqual(['1'])
    expect(sockets).toHaveLength(1)
    controller.abort()
    await adapter.stop()
  })

  it('a stale error from a superseded runtime never evicts its replacement', async () => {
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries.length = 0
    let releaseIngress!: () => void
    let markIngressStarted!: () => void
    transport.ingressGate = new Promise<void>((resolve) => {
      releaseIngress = resolve
    })
    const ingressStarted = new Promise<void>((resolve) => {
      markIngressStarted = resolve
    })
    transport.ingressStarted = markIngressStarted
    const sockets: FakeSocket[] = []
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => {
        const socket = new FakeSocket()
        sockets.push(socket)
        return socket
      },
      webFactory: () => new FakeWeb(),
      discoveryIntervalMs: 60_000,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)
    expect(sockets).toHaveLength(1)

    // Runtime A stalls inside ingress with an open event.
    const pending = sockets[0]!.emit({
      team_id: 'T',
      api_app_id: 'A1',
      event: { type: 'message', channel: 'D', ts: '1', user: 'U', text: 'first' },
    })
    await ingressStarted

    // Discovery evicts A, then re-acquires the same target as runtime B.
    transport.connections = []
    await adapter.refreshConnections(controller.signal)
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    await adapter.refreshConnections(controller.signal)
    expect(sockets).toHaveLength(2)
    expect(sockets[1]!.started).toBe(true)

    // A's stalled ingress fails stale: its removal must evict A but never B.
    transport.ingressError = new LeaseStaleError()
    releaseIngress()
    await pending
    transport.ingressError = undefined

    expect(sockets[0]!.disconnected).toBe(true)
    expect(sockets[1]!.disconnected).toBe(false)
    // B still owns the runtime: its socket events keep being forwarded.
    await sockets[1]!.emit({
      team_id: 'T',
      api_app_id: 'A1',
      event: { type: 'message', channel: 'D', ts: '2', user: 'U', text: 'second' },
    })
    expect(transport.envelopes.map((envelope) => envelope.messageTs)).toEqual(['1', '2'])
    controller.abort()
    await adapter.stop()
  })

  it('stops an old drain before claim, mutation, or acknowledgement when renewal expires', async () => {
    vi.useFakeTimers()
    const transport = new FakeTransport()
    transport.connections = [{ projectId: 'p', connectionId: 'c' }]
    transport.deliveries.length = 0
    const web = new FakeWeb()
    const adapter = new SlackAdapter({
      adapterId: 'a',
      transport,
      socketFactory: () => new FakeSocket(),
      webFactory: () => web,
      heartbeatIntervalMs: 1_000,
      deliveryPollIntervalMs: 100,
    })
    const controller = new AbortController()
    await adapter.start(controller.signal)
    const claimsBefore = transport.claimDeliveryCalls
    transport.deliveries.push({
      id: 'old-delivery',
      conversationId: 'D',
      threadTs: null,
      payloadJson: JSON.stringify({ text: 'old' }),
    })
    let releaseUncertain!: () => void
    let markUncertainStarted!: () => void
    transport.uncertainGate = new Promise<void>((resolve) => {
      releaseUncertain = resolve
    })
    const uncertainStarted = new Promise<void>((resolve) => {
      markUncertainStarted = resolve
    })
    transport.uncertainStarted = markUncertainStarted

    vi.advanceTimersByTime(100)
    await uncertainStarted
    transport.nextRenewals = [null]
    await vi.advanceTimersByTimeAsync(900)
    releaseUncertain()
    await vi.advanceTimersByTimeAsync(0)

    expect(transport.claimDeliveryCalls).toBe(claimsBefore)
    expect(web.posted).toEqual([])
    expect(transport.acks).toEqual([])
    controller.abort()
    await adapter.stop()
  })
})
