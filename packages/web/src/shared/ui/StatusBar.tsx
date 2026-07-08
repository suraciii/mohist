interface StatusBarProps {
  active: number
  waiting: number
  completed: number
  failed: number
  activeSlots: number
  maxSlots: number
  children?: React.ReactNode
}

const counts = [
  { key: 'active', label: 'Active', color: 'bg-blue-100 text-blue-700' },
  { key: 'waiting', label: 'Waiting', color: 'bg-amber-100 text-amber-700' },
  { key: 'completed', label: 'Completed', color: 'bg-green-100 text-green-700' },
  { key: 'failed', label: 'Failed', color: 'bg-red-100 text-red-700' },
] as const

export function StatusBar({ active, waiting, completed, failed, activeSlots, maxSlots, children }: StatusBarProps) {
  const values = { active, waiting, completed, failed }

  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-2 px-4 py-3 md:px-6 bg-white border-b border-gray-200">
      {counts.map(({ key, label, color }) => (
        <span key={key} className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-semibold ${color}`}>
          <span>{label}:</span>
          <span>{values[key]}</span>
        </span>
      ))}
      {children}
      <span className="ml-auto text-xs text-gray-500 font-medium">
        {activeSlots}/{maxSlots} slots used
      </span>
    </div>
  )
}
