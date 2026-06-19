import { useEffect, useRef, useState } from 'react'
import { HubConnectionBuilder, LogLevel, HubConnection, HubConnectionState } from '@microsoft/signalr'
import { EVENT_TYPES } from '../lib/canonical-event-types'

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

export function useEventsConnection(
  projectId: string | null,
  onEvent: EventCallback,
  onTranscriptEvent?: TranscriptCallback,
): ConnectionStatus {
  const callbackRef = useRef(onEvent)
  callbackRef.current = onEvent
  const transcriptCallbackRef = useRef(onTranscriptEvent)
  transcriptCallbackRef.current = onTranscriptEvent
  const [status, setStatus] = useState<ConnectionStatus>('connecting')

  useEffect(() => {
    if (!projectId) {
      setStatus('disconnected')
      return
    }

    const connection = createEventsConnection(projectId)

    const handleState = (next: ConnectionStatus) => {
      setStatus(next)
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
  }, [projectId])

  return status
}
