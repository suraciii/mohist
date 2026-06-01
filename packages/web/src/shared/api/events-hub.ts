import { useEffect, useRef } from 'react'
import { HubConnectionBuilder, LogLevel, HubConnection } from '@microsoft/signalr'

const HUB_URL = '/hubs/events'

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

type EventCallback = (eventName: string, data: unknown) => void

export function useEventsConnection(
  projectId: string | null,
  onEvent: EventCallback,
): void {
  const callbackRef = useRef(onEvent)
  callbackRef.current = onEvent

  useEffect(() => {
    if (!projectId) return

    const connection = createEventsConnection(projectId)
    connection.on('OnEvent', (eventName: string, data: unknown) => {
      callbackRef.current(eventName, data)
    })

    connection
      .start()
      .catch((err) => {
        console.error('[EventsHub] Connection failed:', err)
      })

    return () => {
      connection.stop().catch(() => {})
    }
  }, [projectId])
}
