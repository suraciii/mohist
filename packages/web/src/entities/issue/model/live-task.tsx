import { createContext, useContext } from 'react'
import type { RebaseConflictState } from './types'

export interface LiveTaskState {
  activeTaskId: string | null
  activeTaskElapsedMs: number | null
  rebaseConflict: RebaseConflictState | null
  eventsReconnectVersion: number
}

export const LiveTaskContext = createContext<LiveTaskState>({
  activeTaskId: null,
  activeTaskElapsedMs: null,
  rebaseConflict: null,
  eventsReconnectVersion: 0,
})

export function useLiveTask(): LiveTaskState {
  return useContext(LiveTaskContext)
}
