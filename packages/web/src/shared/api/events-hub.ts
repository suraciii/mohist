import { useEffect, useRef } from 'react'
import { HubConnectionBuilder, LogLevel, HubConnection } from '@microsoft/signalr'
import { EVENT_TYPES } from '../lib/canonical-event-types'

const HUB_URL = '/hubs/events'
const SUBSCRIBE_METHOD = 'SetSubscriptionsAsync'
const TRANSCRIPT_EVENT = 'OnTranscriptEvent'

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

/**
 * Apply the canonical event-type list to the given SignalR connection.
 *
 * `SetSubscriptionsAsync` is idempotent on the server (`MohistHub` replaces
 * the per-connection set wholesale), so re-invoking on reconnect or after a
 * hot reload is safe. Invoke failures are caught and logged: a transient
 * invoke error MUST NOT tear down an otherwise-healthy connection.
 */
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

/**
 * Canonical list of every CloudEvent type the Web subscribes to. Shared by
 * `useEventsConnection` (for `SetSubscriptionsAsync`) and `LiveTaskProvider`
 * (for switch exhaustiveness) so the subscription set and the switch cannot
 * drift. See `shared/lib/canonical-event-types.ts` for the source of truth.
 */
export const SUBSCRIPTION_EVENT_TYPES: readonly string[] = EVENT_TYPES

export type EventCallback = (eventName: string, data: unknown) => void

export type TranscriptEnvelope = unknown

export type TranscriptCallback = (envelope: TranscriptEnvelope) => void

export function useEventsConnection(
  projectId: string | null,
  onEvent: EventCallback,
  onTranscriptEvent?: TranscriptCallback,
): void {
  const callbackRef = useRef(onEvent)
  callbackRef.current = onEvent
  const transcriptCallbackRef = useRef(onTranscriptEvent)
  transcriptCallbackRef.current = onTranscriptEvent

  useEffect(() => {
    if (!projectId) return

    const connection = createEventsConnection(projectId)
    connection.on('OnEvent', (eventName: string, data: unknown) => {
      callbackRef.current(eventName, data)
    })
    connection.on(TRANSCRIPT_EVENT, (envelope: TranscriptEnvelope) => {
      const handler = transcriptCallbackRef.current
      if (handler) {
        handler(envelope)
      }
    })
    connection.onreconnected(() => {
      void applySubscription(connection)
    })

    connection
      .start()
      .then(() => {
        void applySubscription(connection)
      })
      .catch((err: unknown) => {
        console.error('[EventsHub] Connection failed:', err)
      })

    return () => {
      connection.stop().catch(() => {})
    }
  }, [projectId])
}
