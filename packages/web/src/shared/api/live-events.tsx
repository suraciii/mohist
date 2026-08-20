import { createContext, useContext, useEffect, useState } from 'react'
import { useQueryClient, type QueryClient } from '@tanstack/react-query'
import { DOMAIN_EVENT_TYPES, TRANSCRIPT_EVENT_TYPES } from '../lib/canonical-event-types'

export type ConnectionStatus = 'connecting' | 'connected' | 'reconnecting' | 'disconnected'

export interface TaskLogDeltaEntryWire {
  seq: number
  timestamp: string
  source: string
  text: string
}

export interface TaskLogDeltaEnvelopeWire {
  ownerKind: string
  ownerId: string
  projectId: string
  workId: string
  taskId: string
  entries: TaskLogDeltaEntryWire[]
  truncated: boolean
}

export interface TaskLogSubscription {
  workflowRunId: string
  taskId: string
}

export interface RegistrationHandle {
  dispose: () => void
}

export type TaskLogRegistration = ({ admitted: true } & RegistrationHandle) | { admitted: false }

export interface LiveEventsApi {
  registerTaskLogScope: (
    scope: TaskLogSubscription,
    onDelta: (delta: TaskLogDeltaEnvelopeWire) => void,
    refetch: () => Promise<unknown>,
  ) => TaskLogRegistration
  registerTranscriptReconciliation: (
    sessionId: string,
    runtimeSessionId: string,
    refetch: () => Promise<unknown>,
  ) => RegistrationHandle
}

const unavailableLiveEvents: LiveEventsApi = {
  registerTaskLogScope: () => ({ admitted: false }),
  registerTranscriptReconciliation: () => ({ dispose: () => {} }),
}

export const LiveEventsContext = createContext<LiveEventsApi | undefined>(undefined)

export function useLiveEvents(): LiveEventsApi {
  return useContext(LiveEventsContext) ?? unavailableLiveEvents
}

interface WebSocketLike {
  readonly readyState: number
  onopen: ((event: Event) => void) | null
  onmessage: ((event: MessageEvent) => void) | null
  onclose: ((event: CloseEvent) => void) | null
  onerror: ((event: Event) => void) | null
  send(data: string): void
  close(code?: number, reason?: string): void
}

export type WebSocketFactory = (url: string) => WebSocketLike

interface TranscriptRegistration {
  sessionId: string
  runtimeSessionId: string
  reconcile: () => Promise<unknown>
}

interface TaskLogRegistrationEntry {
  refs: Map<symbol, { onDelta: (delta: TaskLogDeltaEnvelopeWire) => void; refetch: () => Promise<unknown> }>
  bufferingGeneration: number | null
  buffered: TaskLogDeltaEnvelopeWire[]
}

interface TranscriptBuffer {
  generation: number
  events: Record<string, unknown>[]
}

interface LiveEventsControllerOptions {
  projectId: string
  queryClient: QueryClient
  onDomainEvent: (eventName: string, event: unknown) => void
  onTranscriptEvent: (event: unknown) => void
  onStatus: (status: ConnectionStatus) => void
  onAcknowledged: () => void
  createWebSocket?: WebSocketFactory
  setTimer?: typeof setTimeout
  clearTimer?: typeof clearTimeout
  random?: () => number
  location?: Pick<Location, 'protocol' | 'host'>
}

const MAX_TASK_LOG_SCOPES = 128
const OPEN = 1

function taskScopeKey(scope: TaskLogSubscription): string {
  return `${scope.workflowRunId}\u0000${scope.taskId}`
}

function transcriptIdentityKey(sessionId: string, runtimeSessionId: string): string {
  return `${sessionId}\u0000${runtimeSessionId}`
}

function socketUrl(projectId: string, location: Pick<Location, 'protocol' | 'host'>): string {
  const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:'
  return `${protocol}//${location.host}/api/projects/${encodeURIComponent(projectId)}/events/socket`
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === 'object' && !Array.isArray(value)
}

function hasProjectId(value: unknown, projectId: string): boolean {
  if (value === projectId) return true
  if (Array.isArray(value)) return value.some((entry) => hasProjectId(entry, projectId))
  if (isRecord(value)) return Object.values(value).some((entry) => hasProjectId(entry, projectId))
  return false
}

function isReconciledStreamQuery(queryKey: readonly unknown[]): boolean {
  return queryKey.includes('transcript') || queryKey.includes('task-log')
}

export class LiveEventsController implements LiveEventsApi {
  private readonly options: Required<
    Pick<LiveEventsControllerOptions, 'createWebSocket' | 'setTimer' | 'clearTimer' | 'random' | 'location'>
  > &
    LiveEventsControllerOptions
  private socket: WebSocketLike | null = null
  private stopped = false
  private reconnectAttempt = 0
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null
  private generation = 0
  private requestSequence = 0
  private inFlightId: string | null = null
  private subscriptionDirty = false
  private taskLogs = new Map<string, TaskLogRegistrationEntry>()
  private transcripts = new Map<symbol, TranscriptRegistration>()
  private transcriptBuffers = new Map<string, TranscriptBuffer>()

  constructor(options: LiveEventsControllerOptions) {
    this.options = {
      ...options,
      createWebSocket: options.createWebSocket ?? ((url) => new WebSocket(url)),
      setTimer: options.setTimer ?? setTimeout,
      clearTimer: options.clearTimer ?? clearTimeout,
      random: options.random ?? Math.random,
      location: options.location ?? window.location,
    }
  }

  start(): void {
    this.stopped = false
    this.connect()
  }

  stop(): void {
    this.stopped = true
    this.generation += 1
    if (this.reconnectTimer !== null) {
      this.options.clearTimer(this.reconnectTimer)
      this.reconnectTimer = null
    }
    const socket = this.socket
    this.socket = null
    this.inFlightId = null
    if (socket) socket.close(1000, 'client shutdown')
    this.options.onStatus('disconnected')
  }

  registerTaskLogScope(
    scope: TaskLogSubscription,
    onDelta: (delta: TaskLogDeltaEnvelopeWire) => void,
    refetch: () => Promise<unknown>,
  ): TaskLogRegistration {
    const key = taskScopeKey(scope)
    let entry = this.taskLogs.get(key)
    if (!entry) {
      if (this.taskLogs.size >= MAX_TASK_LOG_SCOPES) return { admitted: false }
      entry = { refs: new Map(), bufferingGeneration: null, buffered: [] }
      this.taskLogs.set(key, entry)
    }
    const token = Symbol(key)
    entry.refs.set(token, { onDelta, refetch })
    if (entry.refs.size === 1) this.queueSubscription()
    let disposed = false
    return {
      admitted: true,
      dispose: () => {
        if (disposed) return
        disposed = true
        const current = this.taskLogs.get(key)
        if (!current) return
        current.refs.delete(token)
        if (current.refs.size === 0) {
          this.taskLogs.delete(key)
          this.queueSubscription()
        }
      },
    }
  }

  registerTranscriptReconciliation(
    sessionId: string,
    runtimeSessionId: string,
    reconcile: () => Promise<unknown>,
  ): RegistrationHandle {
    const token = Symbol(sessionId)
    this.transcripts.set(token, {
      sessionId,
      runtimeSessionId,
      reconcile,
    })
    let disposed = false
    return {
      dispose: () => {
        if (disposed) return
        disposed = true
        this.transcripts.delete(token)
      },
    }
  }

  private connect(): void {
    if (this.stopped) return
    this.options.onStatus(this.generation === 0 ? 'connecting' : 'reconnecting')
    const socket = this.options.createWebSocket(socketUrl(this.options.projectId, this.options.location))
    const generation = ++this.generation
    this.socket = socket
    this.inFlightId = null
    this.subscriptionDirty = true

    socket.onopen = () => {
      if (!this.isCurrent(socket, generation)) return
      this.sendSubscription()
    }
    socket.onmessage = (message) => {
      if (!this.isCurrent(socket, generation) || typeof message.data !== 'string') return
      this.receive(message.data, generation)
    }
    socket.onerror = () => {
      if (this.isCurrent(socket, generation)) socket.close()
    }
    socket.onclose = () => {
      if (!this.isCurrent(socket, generation)) return
      this.socket = null
      this.inFlightId = null
      if (this.stopped) return
      this.options.onStatus('reconnecting')
      const base = Math.min(1000 * 2 ** this.reconnectAttempt++, 30_000)
      const delay = Math.round(base * (0.75 + this.options.random() * 0.5))
      this.reconnectTimer = this.options.setTimer(() => {
        this.reconnectTimer = null
        this.connect()
      }, delay)
    }
  }

  private isCurrent(socket: WebSocketLike, generation: number): boolean {
    return this.socket === socket && this.generation === generation && !this.stopped
  }

  private queueSubscription(): void {
    this.subscriptionDirty = true
    this.sendSubscription()
  }

  private sendSubscription(): void {
    const socket = this.socket
    if (!socket || socket.readyState !== OPEN || this.inFlightId !== null || !this.subscriptionDirty) return
    this.subscriptionDirty = false
    const id = `req_${++this.requestSequence}`
    this.inFlightId = id
    const taskLogs = [...this.taskLogs.keys()].map((key) => {
      const [workflowRunId, taskId] = key.split('\u0000')
      return { workflowRunId, taskId }
    })
    socket.send(
      JSON.stringify({
        jsonrpc: '2.0',
        id,
        method: 'subscription.set',
        params: {
          domain: { types: [...DOMAIN_EVENT_TYPES], match: null },
          transcript: { types: [...TRANSCRIPT_EVENT_TYPES] },
          taskLogs,
        },
      }),
    )
  }

  private receive(raw: string, generation: number): void {
    let message: unknown
    try {
      message = JSON.parse(raw)
    } catch {
      return
    }
    if (!isRecord(message)) return
    if ('id' in message) {
      void this.receiveResponse(message, generation)
      return
    }
    if (message.jsonrpc !== '2.0') return
    const params = isRecord(message.params) ? message.params : null
    if (message.method === 'event.domain' && params && isRecord(params.event)) {
      const eventName = params.event.type
      if (typeof eventName === 'string') this.options.onDomainEvent(eventName, params.event)
    } else if (message.method === 'event.transcript' && params && isRecord(params.event)) {
      this.receiveTranscript(params.event, generation)
    } else if (message.method === 'event.task-log' && params && isRecord(params.delta)) {
      this.receiveTaskLog(params.delta as unknown as TaskLogDeltaEnvelopeWire)
    }
  }

  private async receiveResponse(response: Record<string, unknown>, generation: number): Promise<void> {
    if (response.id !== this.inFlightId) return
    const hasResult = Object.prototype.hasOwnProperty.call(response, 'result')
    const hasError = Object.prototype.hasOwnProperty.call(response, 'error')
    if (response.jsonrpc !== '2.0' || hasResult === hasError || (hasResult && !isRecord(response.result))) {
      this.socket?.close(1008, 'invalid subscription response')
      return
    }
    if (hasError) {
      this.socket?.close(1008, 'subscription rejected')
      return
    }
    this.reconnectAttempt = 0
    this.options.onStatus('connected')
    this.options.onAcknowledged()
    await this.reconcile(generation)
    if (!this.isActiveGeneration(generation)) return
    this.inFlightId = null
    this.sendSubscription()
  }

  private async reconcile(generation: number): Promise<void> {
    if (!this.isActiveGeneration(generation)) return
    const bufferedTranscriptIdentities = new Set<string>()
    for (const registration of this.transcripts.values()) {
      const key = transcriptIdentityKey(registration.sessionId, registration.runtimeSessionId)
      if (bufferedTranscriptIdentities.has(key)) continue
      bufferedTranscriptIdentities.add(key)
      this.transcriptBuffers.set(key, { generation, events: [] })
    }
    for (const entry of this.taskLogs.values()) {
      entry.bufferingGeneration = generation
      entry.buffered = []
    }
    await this.options.queryClient.invalidateQueries({
      predicate: (query) =>
        query.getObserversCount() > 0 &&
        hasProjectId(query.queryKey, this.options.projectId) &&
        !isReconciledStreamQuery(query.queryKey),
    })
    if (!this.isActiveGeneration(generation)) return

    const transcriptReconciliations = [...this.transcripts.entries()].map(async ([token, registration]) => {
      if (!this.isActiveGeneration(generation) || this.transcripts.get(token) !== registration) return
      await registration.reconcile()
    })
    const taskRefetches = [...this.taskLogs.entries()].flatMap(([key, entry]) =>
      [...entry.refs.entries()].map(async ([token, registration]) => {
        if (!this.isActiveGeneration(generation) || this.taskLogs.get(key) !== entry || !entry.refs.has(token)) return
        await registration.refetch()
      }),
    )
    await Promise.allSettled([...transcriptReconciliations, ...taskRefetches])
    if (!this.isActiveGeneration(generation)) return

    for (const [key, buffer] of this.transcriptBuffers) {
      if (buffer.generation !== generation) continue
      this.transcriptBuffers.delete(key)
      const [sessionId, runtimeSessionId] = key.split('\u0000')
      const stillRegistered = [...this.transcripts.values()].some(
        (registration) => registration.sessionId === sessionId && registration.runtimeSessionId === runtimeSessionId,
      )
      if (!stillRegistered) continue
      for (const event of buffer.events) {
        if (!this.isActiveGeneration(generation)) return
        this.options.onTranscriptEvent(event)
      }
    }
    for (const [key, entry] of this.taskLogs) {
      if (entry.bufferingGeneration !== generation) continue
      entry.bufferingGeneration = null
      const buffered = entry.buffered
      entry.buffered = []
      for (const delta of buffered) {
        if (!this.isActiveGeneration(generation) || this.taskLogs.get(key) !== entry) return
        for (const { onDelta } of entry.refs.values()) onDelta(delta)
      }
    }
  }

  private receiveTranscript(event: Record<string, unknown>, generation: number): void {
    const sessionId = event.sessionId
    const runtimeSessionId = event.runtimeSessionId
    if (typeof sessionId !== 'string' || typeof runtimeSessionId !== 'string') return
    const buffer = this.transcriptBuffers.get(transcriptIdentityKey(sessionId, runtimeSessionId))
    if (buffer?.generation === generation) buffer.events.push(event)
    else this.options.onTranscriptEvent(event)
  }

  private receiveTaskLog(delta: TaskLogDeltaEnvelopeWire): void {
    if (delta.ownerKind !== 'workflow' || delta.projectId !== this.options.projectId) return
    const entry = this.taskLogs.get(taskScopeKey({ workflowRunId: delta.ownerId, taskId: delta.taskId }))
    if (!entry) return
    if (entry.bufferingGeneration === this.generation) {
      entry.buffered.push(delta)
      return
    }
    for (const { onDelta } of entry.refs.values()) onDelta(delta)
  }

  private isActiveGeneration(generation: number): boolean {
    return !this.stopped && this.socket !== null && this.generation === generation
  }
}

export type EventCallback = (eventName: string, data: unknown) => void
export type TranscriptCallback = (envelope: unknown) => void

export interface EventsConnection {
  status: ConnectionStatus
  reconnectVersion: number
  api: LiveEventsApi
}

export function useEventsConnection(
  projectId: string | null,
  onEvent: EventCallback,
  onTranscriptEvent?: TranscriptCallback,
): EventsConnection {
  const queryClient = useQueryClient()
  const [status, setStatus] = useState<ConnectionStatus>(projectId ? 'connecting' : 'disconnected')
  const [reconnectVersion, setReconnectVersion] = useState(0)
  const [api, setApi] = useState<LiveEventsApi>(unavailableLiveEvents)

  useEffect(() => {
    if (!projectId) {
      setStatus('disconnected')
      setApi(unavailableLiveEvents)
      return
    }
    const controller = new LiveEventsController({
      projectId,
      queryClient,
      onDomainEvent: onEvent,
      onTranscriptEvent: (event) => onTranscriptEvent?.(event),
      onStatus: setStatus,
      onAcknowledged: () => setReconnectVersion((version) => version + 1),
    })
    setApi(controller)
    controller.start()
    return () => controller.stop()
  }, [projectId, queryClient])

  return { status, reconnectVersion, api }
}
