import { useState } from 'react'
import Markdown from 'react-markdown'
import type { ApprovalDimension, ApprovalOutput } from '../lib/types'

interface ReviewSummaryProps {
  output: ApprovalOutput
}

function VerdictBadge({ verdict }: { verdict: 'PASS' | 'FAIL' | 'REVIEW' }) {
  if (verdict === 'PASS') {
    return (
      <span className="inline-flex items-center gap-1.5 rounded-full bg-green-100 px-4 py-1.5 text-lg font-bold text-green-800 ring-2 ring-green-300">
        <svg className="h-5 w-5 text-green-600" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
        </svg>
        PASS
      </span>
    )
  }

  if (verdict === 'FAIL') {
    return (
      <span className="inline-flex items-center gap-1.5 rounded-full bg-red-100 px-4 py-1.5 text-lg font-bold text-red-800 ring-2 ring-red-300">
        <svg className="h-5 w-5 text-red-600" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
        </svg>
        FAIL
      </span>
    )
  }

  return (
    <span className="inline-flex items-center gap-1.5 rounded-full bg-gray-100 px-4 py-1.5 text-lg font-bold text-gray-600 ring-2 ring-gray-200">
      <svg className="h-5 w-5 text-gray-400" viewBox="0 0 20 20" fill="currentColor">
        <path fillRule="evenodd" d="M18 10a8 8 0 11-16 0 8 8 0 0116 0zm-8-5a.75.75 0 01.75.75v4.5a.75.75 0 01-1.5 0v-4.5A.75.75 0 0110 5zm0 10a1 1 0 100-2 1 1 0 000 2z" clipRule="evenodd" />
      </svg>
      REVIEW
    </span>
  )
}

function DimensionGrid({ dimensions }: { dimensions: ApprovalDimension[] }) {
  return (
    <div className="space-y-2">
      {dimensions.map((dim) => (
        <div key={dim.name} className="rounded-md border border-gray-200 bg-white p-2.5">
          <div className="flex items-center gap-2">
            {dim.status === 'PASS' ? (
              <svg className="h-4 w-4 flex-shrink-0 text-green-500" viewBox="0 0 20 20" fill="currentColor">
                <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
              </svg>
            ) : (
              <svg className="h-4 w-4 flex-shrink-0 text-red-500" viewBox="0 0 20 20" fill="currentColor">
                <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
              </svg>
            )}
            <span className="text-sm font-medium text-gray-700">{dim.name}</span>
            <span
              className={`ml-auto text-xs font-semibold px-1.5 py-0.5 rounded ${
                dim.status === 'PASS'
                  ? 'bg-green-50 text-green-700'
                  : 'bg-red-50 text-red-700'
              }`}
            >
              {dim.status}
            </span>
          </div>
          {dim.status === 'FAIL' && dim.issues && dim.issues.length > 0 && (
            <ul className="mt-1.5 ml-6 space-y-0.5">
              {dim.issues.map((issue, i) => (
                <li key={i} className="text-xs text-red-600">
                  {issue}
                </li>
              ))}
            </ul>
          )}
        </div>
      ))}
    </div>
  )
}

export function ReviewSummary({ output }: ReviewSummaryProps) {
  const [expanded, setExpanded] = useState(false)

  const verdict: 'PASS' | 'FAIL' | 'REVIEW' =
    output.verdict === 'PASS'
      ? 'PASS'
      : output.verdict === 'FAIL'
        ? 'FAIL'
        : 'REVIEW'

  const dimensions = output.dimensions && output.dimensions.length > 0
    ? output.dimensions
    : null

  const reportContent =
    output.reviewReport ||
    output.selfReviewNotes ||
    null

  const hasReport = reportContent != null && reportContent.trim().length > 0

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-center">
        <VerdictBadge verdict={verdict} />
      </div>

      {verdict !== 'REVIEW' && dimensions && (
        <DimensionGrid dimensions={dimensions} />
      )}

      {hasReport && (
        <div>
          <button
            onClick={() => setExpanded((prev) => !prev)}
            className="w-full rounded-md border border-gray-200 bg-white px-3 py-2 text-sm font-medium text-gray-600 hover:bg-gray-50 transition-colors flex items-center justify-center gap-2"
          >
            <svg
              className={`h-4 w-4 text-gray-400 transition-transform ${expanded ? 'rotate-90' : ''}`}
              viewBox="0 0 20 20"
              fill="currentColor"
            >
              <path
                fillRule="evenodd"
                d="M7.21 14.77a.75.75 0 01.02-1.06L11.168 10 7.23 6.29a.75.75 0 111.04-1.08l4.5 4.25a.75.75 0 010 1.08l-4.5 4.25a.75.75 0 01-1.06-.02z"
                clipRule="evenodd"
              />
            </svg>
            {expanded ? 'Hide Full Report' : 'View Full Report'}
          </button>
          {expanded && (
            <div className="mt-2 rounded-md border border-gray-200 bg-white p-4 prose prose-sm max-w-none prose-gray">
              <Markdown>{reportContent}</Markdown>
            </div>
          )}
        </div>
      )}
    </div>
  )
}
