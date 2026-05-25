import { Link } from 'react-router-dom'

export function NotFoundPage() {
  return (
    <div className="flex items-center justify-center flex-1">
      <div className="text-center">
        <div className="text-gray-400 text-lg mb-4">Page not found</div>
        <Link to="/" className="text-blue-600 hover:text-blue-700 text-sm">
          Back to board
        </Link>
      </div>
    </div>
  )
}
