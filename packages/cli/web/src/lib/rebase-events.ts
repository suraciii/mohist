export type RebaseEvent =
  | { type: 'rebase_started'; issueNumber: number }
  | { type: 'rebase_progress'; issueNumber: number; step: 'fetching' | 'checking' | 'rebasing' | 'verifying' }
  | { type: 'rebase_completed'; issueNumber: number; rebased: boolean }
  | { type: 'rebase_conflict'; issueNumber: number; conflicts: string[] }

const target = new EventTarget()

export function dispatchRebaseEvent(event: RebaseEvent): void {
  target.dispatchEvent(new CustomEvent('rebase-event', { detail: event }))
}

export function onRebaseEvent(handler: (event: RebaseEvent) => void): () => void {
  const listener = (e: Event) => {
    handler((e as CustomEvent<RebaseEvent>).detail)
  }
  target.addEventListener('rebase-event', listener)
  return () => target.removeEventListener('rebase-event', listener)
}
