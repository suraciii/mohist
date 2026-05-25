import type { CoderSessionSummary } from '../lib/types'

interface SessionDetailProps {
  session: CoderSessionSummary
}

export function SessionDetail({ session: _session }: SessionDetailProps) {
  return (
    <div className="px-3 py-1.5 border-t border-gray-100">
      <span className="text-xs text-gray-400">Session info</span>
    </div>
  )
}
