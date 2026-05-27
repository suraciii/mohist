import { createContext, useContext } from 'react'
import type { LiveTaskState } from '../api/types'

export const LiveTaskContext = createContext<LiveTaskState>({
  activeTaskId: null,
  activeTaskElapsedMs: null,
  rebaseConflict: null,
})

export function useLiveTask(): LiveTaskState {
  return useContext(LiveTaskContext)
}
