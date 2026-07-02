import { useEffect, useRef, type MutableRefObject } from 'react'

/**
 * Read the issue number from `window.location.pathname` (e.g. `/proj/issues/42`).
 * Returns `null` when the pathname does not match `/issues/<number>` or the
 * captured segment is not numeric.
 *
 * Used by the realtime layer to suppress in-app notices when the user is
 * already viewing the issue the event is about.
 *
 * Kept export-pure so tests can pin behavior without mounting a router.
 */
export function getCurrentIssueNumber(): number | null {
  const match = window.location.pathname.match(/\/issues\/(\d+)/)
  return match ? parseInt(match[1], 10) : null
}

/**
 * Provide a stable ref to the currently-viewed issue number, kept in sync
 * with both `history.pushState` / `history.replaceState` calls and the
 * browser `popstate` event. The ref is initialized from `getCurrentIssueNumber()`
 * on first mount.
 *
 * The monkey-patch effect wraps `pushState` / `replaceState` to update the
 * ref after every navigation; on cleanup the originals are restored so the
 * effect is fully reversible.
 *
 * Used by toast helpers (lifecycle + inbox branch) that need the latest
 * viewed-issue at call time without re-subscribing on every render. The
 * caller reads `.current` at the call site and passes it as the
 * `viewedIssue` parameter (D5).
 */
export function useViewedIssueRef(): MutableRefObject<number | null> {
  const viewedIssueRef = useRef<number | null>(getCurrentIssueNumber())

  useEffect(() => {
    const update = () => {
      viewedIssueRef.current = getCurrentIssueNumber()
    }
    window.addEventListener('popstate', update)
    const origPush = history.pushState
    const origReplace = history.replaceState
    history.pushState = function (...args) {
      origPush.apply(this, args)
      update()
    }
    history.replaceState = function (...args) {
      origReplace.apply(this, args)
      update()
    }
    return () => {
      window.removeEventListener('popstate', update)
      history.pushState = origPush
      history.replaceState = origReplace
    }
  }, [])

  return viewedIssueRef
}