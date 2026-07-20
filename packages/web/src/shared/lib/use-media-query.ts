import { useEffect, useState } from 'react'

let matchesOverride: boolean | null = null
const overrideListeners = new Set<(matches: boolean | null) => void>()

export function setMatchesForTest(matches: boolean | null): void {
  matchesOverride = matches
  for (const listener of overrideListeners) listener(matches)
}

function getLiveMatches(query: string): boolean {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
    return false
  }
  return window.matchMedia(query).matches
}

export function useMediaQuery(query: string): boolean {
  const [override, setOverride] = useState<boolean | null>(() => matchesOverride)
  const [liveMatches, setLiveMatches] = useState<boolean>(() => (
    matchesOverride === null ? getLiveMatches(query) : false
  ))

  useEffect(() => {
    const listener = (next: boolean | null) => setOverride(next)
    overrideListeners.add(listener)
    if (matchesOverride !== override) setOverride(matchesOverride)
    return () => {
      overrideListeners.delete(listener)
    }
  }, [override])

  useEffect(() => {
    if (override !== null) return

    const mql = window.matchMedia(query)
    const update = () => setLiveMatches(mql.matches)
    update()
    mql.addEventListener('change', update)
    return () => mql.removeEventListener('change', update)
  }, [query, override])

  return override !== null ? override : liveMatches
}
