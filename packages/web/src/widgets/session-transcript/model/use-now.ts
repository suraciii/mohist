import { useEffect, useState } from 'react'

export interface UseNowOptions {
  intervalMs: number
  now?: number
  enabled?: boolean
}

export function useNow({ intervalMs, now, enabled = true }: UseNowOptions): number | undefined {
  const [tick, setTick] = useState<number>(() => (now ?? Date.now()))

  useEffect(() => {
    if (now !== undefined) return
    if (!enabled) return
    setTick(Date.now())
    const id = setInterval(() => setTick(Date.now()), intervalMs)
    return () => clearInterval(id)
  }, [intervalMs, now, enabled])

  if (now !== undefined) return now
  if (!enabled) return undefined
  return tick
}
