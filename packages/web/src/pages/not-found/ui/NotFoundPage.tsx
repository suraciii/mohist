import { Link } from 'react-router-dom'
import { NotFoundState } from '@/shared/ui/not-found-state'

export function NotFoundPage() {
  return (
    <NotFoundState
      action={
        <Link to="/" className="text-blue-600 hover:text-blue-700 text-sm">
          Back to board
        </Link>
      }
    />
  )
}
