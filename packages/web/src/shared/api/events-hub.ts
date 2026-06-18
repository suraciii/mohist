import { useContext, useEffect, useRef, useState } from 'react'
import { HubConnectionBuilder, LogLevel, HubConnection, HubConnectionState } from '@microsoft/signalr'
import { EVENT_TYPES } from '../lib/canonical-event-types'
import {
  RuntimeToastContext,
  type RuntimeToastTone,
} from '../ui/toast'

const HUB_URL = '/hubs/events'
const SUBSCRIBE_METHOD = 'SetSubscriptionsAsync'
const TRANSCRIPT_EVENT = 'OnTranscriptEvent'

export type ConnectionStatus = 'connecting' | 'connected' | 'reconnecting' | 'disconnected'

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

export interface UseEventsConnectionOptions {
  /**
   * When true, the hook pushes a runtime toast on every state transition
   * (connecting → connected, connected → reconnecting, etc.). Defaults to
   * true so transport notices surface without callers opting in.
   */
  publishToasts?: boolean
}

export function useEventsConnection(
  projectId: string | null,
  onEvent: EventCallback,
  onTranscriptEvent?: TranscriptCallback,
  options: UseEventsConnectionOptions = {},
): ConnectionStatus {
  const { publishToasts = true } = options
  const callbackRef = useRef(onEvent)
  callbackRef.current = onEvent
  const transcriptCallbackRef = useRef(onTranscriptEvent)
  transcriptCallbackRef.current = onTranscriptEvent
  const toastCtx = useContext(RuntimeToastContext)
  const toastCtxRef = useRef(toastCtx)
  toastCtxRef.current = toastCtx
  const [status, setStatus] = useState<ConnectionStatus>('connecting')
  const lastReportedRef = useRef<ConnectionStatus | null>(null)

  useEffect(() => {
    if (!projectId) {
      setStatus('disconnected')
      return
    }

    const connection = createEventsConnection(projectId)

    const handleState = (next: ConnectionStatus) => {
      setStatus(next)
      if (lastReportedRef.current === next) return
      lastReportedRef.current = next
      if (publishToasts && toastCtxRef.current) {
        toastCtxRef.current.push(buildNoticeForStatus(next))
      }
    }

    connection.on('OnEvent', (eventName: string, data: unknown) => {
      callbackRef.current(eventName, data)
    })
    connection.on(TRANSCRIPT_EVENT, (envelope: TranscriptEnvelope) => {
      const handler = transcriptCallbackRef.current
      if (handler) handler(envelope)
    })

    connection.onreconnecting(() => handleState('reconnecting'))
    connection.onreconnected(() => {
      handleState('connected')
      void applySubscription(connection)
    })
    connection.onclose(() => handleState('disconnected'))

    handleState(mapHubState(connection.state))

    connection
      .start()
      .then(() => {
        handleState('connected')
        void applySubscription(connection)
      })
      .catch((err: unknown) => {
        handleState('disconnected')
        console.error('[EventsHub] Connection failed:', err)
      })

    return () => {
      connection.onreconnecting(() => {})
      connection.onreconnected(() => {})
      connection.onclose(() => {})
      connection.stop().catch(() => {})
    }
  }, [projectId, publishToasts])

  return status
}

interface ToastPushInput {
  tone: RuntimeToastTone
  title: string
  body?: string
  testId: string
}

export function buildNoticeForStatus(status: ConnectionStatus): ToastPushInput {
  switch (status) {
    case 'connecting':
      return {
        tone: 'transport',
        title: 'Connecting to live events',
        body: 'Re-establishing the SignalR connection…',
        testId: 'runtime-toast-connection-connecting',
      }
    case 'connected':
      return {
        tone: 'transport',
        title: 'Live events reconnected',
        body: 'Issue updates will resume streaming.',
        testId: 'runtime-toast-connection-connected',
      }
    case 'reconnecting':
      return {
        tone: 'transport',
        title: 'Reconnecting…',
        body: 'Live events briefly lost. Recent updates may be delayed.',
        testId: 'runtime-toast-connection-reconnecting',
      }
    case 'disconnected':
    default:
      return {
        tone: 'transport',
        title: 'Live events disconnected',
        body: 'Connection dropped. Activity continues to update in the background.',
        testId: 'runtime-toast-connection-disconnected',
      }
  }
}
