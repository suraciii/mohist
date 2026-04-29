import { useState, Children } from 'react'
import type { ReactNode } from 'react'

const DEFAULT_VISIBLE_COUNT = 5

interface ProviderGroupProps {
  label: string
  count: number
  expanded?: boolean
  children: ReactNode
}

export function ProviderGroup({ label, count, expanded: forceExpanded, children }: ProviderGroupProps) {
  const [internalExpanded, setInternalExpanded] = useState(false)

  if (count === 0) return null

  const isExpanded = forceExpanded ?? internalExpanded
  const showToggle = count > DEFAULT_VISIBLE_COUNT
  const childArray = Children.toArray(children)
  const visible = isExpanded ? childArray : childArray.slice(0, DEFAULT_VISIBLE_COUNT)

  return (
    <div>
      <h3 className="text-sm font-medium text-gray-900 mb-3">
        {label}
      </h3>

      <div className="space-y-3">
        {visible}
      </div>

      {showToggle && (
        <button
          onClick={() => setInternalExpanded(prev => !prev)}
          className="mt-3 w-full rounded-md px-3 py-1.5 text-xs font-medium text-gray-500 hover:text-gray-700 hover:bg-gray-50 transition-colors"
        >
          {isExpanded ? 'Show less' : `Show all (${count})`}
        </button>
      )}
    </div>
  )
}
