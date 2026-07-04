import { useEffect, useRef, useState } from 'react'
import { HubConnectionBuilder, LogLevel, HubConnection, HubConnectionState } from '@microsoft/signalr'
import { EVENT_TYPES } from '../lib/canonical-event-types'

const HUB_URL = '/hubs/events'
const SUBSCRIBE_METHOD = 'SetSubscriptionsAsync'
const TRANSCRIPT_EVENT = 'OnTranscriptEvent'
const TASK_LOG_DELTA_EVENT = 'OnTaskLogDelta'
const SUBSCRIBE_TASK_LOG_METHOD = 'SubscribeTaskLogAsync'
const UNSUBSCRIBE_TASK_LOG_METHOD = 'UnsubscribeTaskLogAsync'

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
  projectId?: string | null
  workId: string
  taskId: string | null
  entries: TaskLogDeltaEntryWire[]
  truncated: boolean
}

export interface TaskLogSubscription {
  workflowRunId: string
  taskId: string
}

export function createEventsConnection(projectId: string): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(`${HUB_URL}?projectId=${encodeURIComponent(projectId)}`)
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: ({ previousRetryCount }) =>
        Math.min(1000 * Math.pow(2, previousRetryCount), 30_000),
    })
    .configureLogging(LogLevel.Warning)
    .build()
}

function mapHubState(state: HubConnectionState): ConnectionStatus {
  switch (state) {
    case HubConnectionState.Connected:
      return 'connected'
    case HubConnectionState.Reconnecting:
      return 'reconnecting'
    case HubConnectionState.Connecting:
      return 'connecting'
    case HubConnectionState.Disconnected:
    case HubConnectionState.Disconnecting:
    default:
      return 'disconnected'
  }
}

async function applySubscription(connection: HubConnection): Promise<void> {
  try {
    await connection.invoke(SUBSCRIBE_METHOD, [...EVENT_TYPES])
  } catch (err) {
    console.warn(
      `[EventsHub] ${SUBSCRIBE_METHOD} invoke failed (will not break connection):`,
      err,
    )
  }
}

export const SUBSCRIPTION_EVENT_TYPES: readonly string[] = EVENT_TYPES

export type EventCallback = (eventName: string, data: unknown) => void

export type TranscriptEnvelope = unknown

export type TranscriptCallback = (envelope: TranscriptEnvelope) => void

export type TaskLogDeltaCallback = (envelope: TaskLogDeltaEnvelopeWire) => void

export interface EventsConnection {
  status: ConnectionStatus
  connection: HubConnection | null
  reconnectVersion: number
}

export interface EventsConnectionOptions {
  applyDefaultSubscriptions?: boolean
}

export function useEventsConnection(
  projectId: string | null,
  onEvent: EventCallback,
  onTranscriptEvent?: TranscriptCallback,
  onTaskLogDelta?: TaskLogDeltaCallback,
  options: EventsConnectionOptions = {},
): EventsConnection {
  const applyDefaultSubscriptions = options.applyDefaultSubscriptions ?? true
  const callbackRef = useRef(onEvent)
  callbackRef.current = onEvent
  const transcriptCallbackRef = useRef(onTranscriptEvent)
  transcriptCallbackRef.current = onTranscriptEvent
  const taskLogDeltaCallbackRef = useRef(onTaskLogDelta)
  taskLogDeltaCallbackRef.current = onTaskLogDelta
  const [status, setStatus] = useState<ConnectionStatus>('connecting')
  const [connection, setConnection] = useState<HubConnection | null>(null)
  const [reconnectVersion, setReconnectVersion] = useState(0)

  useEffect(() => {
    if (!projectId) {
      setStatus('disconnected')
      setConnection(null)
      return
    }

    const conn = createEventsConnection(projectId)

    const handleState = (next: ConnectionStatus) => {
      setStatus(next)
    }

    conn.on('OnEvent', (eventName: string, data: unknown) => {
      callbackRef.current(eventName, data)
    })
    conn.on(TRANSCRIPT_EVENT, (envelope: TranscriptEnvelope) => {
      const handler = transcriptCallbackRef.current
      if (handler) handler(envelope)
    })
    conn.on(TASK_LOG_DELTA_EVENT, (envelope: TaskLogDeltaEnvelopeWire) => {
      const handler = taskLogDeltaCallbackRef.current
      if (handler) handler(envelope)
    })

    conn.onreconnecting(() => handleState('reconnecting'))
    conn.onreconnected(() => {
      handleState('connected')
      setReconnectVersion((version) => version + 1)
      if (applyDefaultSubscriptions) void applySubscription(conn)
    })
    conn.onclose(() => {
      handleState('disconnected')
      setConnection(null)
    })

    handleState(mapHubState(conn.state))

    conn
      .start()
      .then(() => {
        handleState('connected')
        setConnection(conn)
        if (applyDefaultSubscriptions) void applySubscription(conn)
      })
      .catch((err: unknown) => {
        handleState('disconnected')
        setConnection(null)
        console.error('[EventsHub] Connection failed:', err)
      })

    return () => {
      conn.onreconnecting(() => {})
      conn.onreconnected(() => {})
      conn.onclose(() => {})
      conn.stop().catch(() => {})
    }
  }, [projectId, applyDefaultSubscriptions])

  return { status, connection, reconnectVersion }
}

export async function subscribeTaskLog(
  connection: HubConnection,
  subscription: TaskLogSubscription,
): Promise<void> {
  try {
    await connection.invoke(
      SUBSCRIBE_TASK_LOG_METHOD,
      subscription.workflowRunId,
      subscription.taskId,
    )
  } catch (err) {
    console.warn(
      `[EventsHub] ${SUBSCRIBE_TASK_LOG_METHOD} invoke failed (will not break connection):`,
      err,
    )
  }
}

export async function unsubscribeTaskLog(
  connection: HubConnection,
  subscription: TaskLogSubscription,
): Promise<void> {
  try {
    await connection.invoke(
      UNSUBSCRIBE_TASK_LOG_METHOD,
      subscription.workflowRunId,
      subscription.taskId,
    )
  } catch (err) {
    console.warn(
      `[EventsHub] ${UNSUBSCRIBE_TASK_LOG_METHOD} invoke failed (will not break connection):`,
      err,
    )
  }
}
