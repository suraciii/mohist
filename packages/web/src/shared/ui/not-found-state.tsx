import type { ReactNode } from 'react'

export interface NotFoundStateProps {
  action?: ReactNode
}

export function NotFoundState({ action }: NotFoundStateProps) {
  return (
    <div className="flex items-center justify-center flex-1">
      <div className="text-center">
        <div className="text-gray-400 text-lg mb-4">Page not found</div>
        {action}
      </div>
    </div>
  )
}
