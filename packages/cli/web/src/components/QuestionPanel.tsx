import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import type { Question } from '../lib/types'
import { api } from '../lib/api'
import { useQuestions } from '../hooks/useQueries'

function formatTime(iso: string): string {
  return new Date(iso).toLocaleString()
}

export function QuestionPanel({ issueId }: { issueId: string }) {
  const queryClient = useQueryClient()
  const [answers, setAnswers] = useState<Record<string, string>>({})

  const replyMutation = useMutation({
    mutationFn: ({ questionId, answer }: { questionId: string; answer: string }) =>
      api.replyQuestion(questionId, answer),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['questions', issueId] })
      setAnswers({})
    },
  })

  const { data: questions, isLoading } = useQuestions(issueId)

  if (isLoading) {
    return (
      <div className="rounded-lg border border-blue-200 bg-blue-50 p-4">
        <h2 className="text-sm font-semibold text-blue-800 mb-2">Questions from Agent</h2>
        <p className="text-sm text-blue-600">Loading...</p>
      </div>
    )
  }

  if (!questions || questions.length === 0) {
    return null
  }

  const pending = questions.filter((q) => q.status === 'pending')

  if (pending.length === 0) {
    return null
  }

  return (
    <div className="rounded-lg border border-blue-200 bg-blue-50 p-4">
      <h2 className="text-sm font-semibold text-blue-800 mb-3">Questions from Agent</h2>
      <div className="space-y-3">
        {pending.map((q) => (
          <QuestionItem
            key={q.id}
            question={q}
            answer={answers[q.id] ?? ''}
            onAnswerChange={(val) => setAnswers({ ...answers, [q.id]: val })}
            onSubmit={() => replyMutation.mutate({ questionId: q.id, answer: answers[q.id]!.trim() })}
            isSubmitting={replyMutation.isPending}
            error={replyMutation.isError ? replyMutation.error.message : undefined}
          />
        ))}
      </div>
    </div>
  )
}

function QuestionItem({
  question,
  answer,
  onAnswerChange,
  onSubmit,
  isSubmitting,
  error,
}: {
  question: Question
  answer: string
  onAnswerChange: (val: string) => void
  onSubmit: () => void
  isSubmitting: boolean
  error?: string
}) {
  return (
    <div className="rounded bg-white p-3">
      <div className="text-xs text-gray-400 mb-1">{formatTime(question.createdAt)}</div>
      <div className="text-sm text-gray-800 mb-3">{question.question}</div>
      <div className="flex gap-2">
        <input
          type="text"
          value={answer}
          onChange={(e) => onAnswerChange(e.target.value)}
          placeholder="Type your answer..."
          className="flex-1 rounded-md border border-gray-300 px-3 py-1.5 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
          onKeyDown={(e) => {
            if (e.key === 'Enter' && answer.trim()) {
              onSubmit()
            }
          }}
        />
        <button
          onClick={onSubmit}
          disabled={!answer.trim() || isSubmitting}
          className="rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
        >
          {isSubmitting ? 'Sending...' : 'Reply'}
        </button>
      </div>
      {error && (
        <div className="mt-1 text-xs text-red-500">{error}</div>
      )}
    </div>
  )
}
