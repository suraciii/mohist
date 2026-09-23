import { createContext, useContext } from 'react'

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
    refetch: (signal: AbortSignal) => Promise<unknown>,
  ) => TaskLogRegistration
  registerTranscriptReconciliation: (
    sessionId: string,
    runtimeSessionId: string | null,
    refetch: (signal: AbortSignal) => Promise<unknown>,
  ) => RegistrationHandle
}

export const unavailableLiveEvents: LiveEventsApi = {
  registerTaskLogScope: () => ({ admitted: false }),
  registerTranscriptReconciliation: () => ({ dispose: () => {} }),
}

export const LiveEventsContext = createContext<LiveEventsApi | undefined>(undefined)

export function useLiveEvents(): LiveEventsApi {
  return useContext(LiveEventsContext) ?? unavailableLiveEvents
}
