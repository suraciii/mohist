interface StickySessionTitleProps {
  title: string
  statusKind: 'loading' | 'live' | 'probing' | 'finalizing' | 'completed' | 'failed' | 'stale'
  turnCount: number
  isRunning: boolean
}

export function StickySessionTitle({ title, statusKind, turnCount, isRunning }: StickySessionTitleProps) {
  return (
    <div className="sticky top-0 z-10 bg-white/95 backdrop-blur-sm border-b border-gray-100 px-4 py-2">
      <div className="max-w-2xl mx-auto flex items-center justify-between">
        <div className="flex items-center gap-2">
          {isRunning && statusKind === 'live' && (
            <span className="relative flex h-2 w-2">
              <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-blue-400 opacity-75" />
              <span className="relative inline-flex rounded-full h-2 w-2 bg-blue-500" />
            </span>
          )}
          <h2 className="text-sm font-medium text-gray-700 truncate max-w-[300px]">{title || 'Session'}</h2>
        </div>
        <div className="flex items-center gap-2 text-xs text-gray-400">
          <span>{turnCount} turn{turnCount !== 1 ? 's' : ''}</span>
          {statusKind === 'live' && <span className="text-blue-500">Running</span>}
          {statusKind === 'finalizing' && <span className="text-yellow-600">Finalizing</span>}
        </div>
      </div>
    </div>
  )
}