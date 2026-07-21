import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { IssueCommentsSection } from './IssueCommentsSection'

describe('IssueCommentsSection keyboard submission', () => {
  it('uses one guarded submit callback for the button and Command+Enter', () => {
    const mutate = vi.fn()
    const mutation = { error: null, isPending: false, mutate } as never
    render(
      <IssueCommentsSection
        comments={[]}
        issueNumber={455}
        issueProjectId="project-1"
        commentText="A useful comment"
        setCommentText={vi.fn()}
        deletingCommentId={null}
        setDeletingCommentId={vi.fn()}
        deleteCommentError={null}
        setDeleteCommentError={vi.fn()}
        mutations={{ addCommentMutation: mutation, deleteCommentMutation: mutation }}
      />,
    )

    const textarea = screen.getByPlaceholderText('Add a comment...')
    fireEvent.keyDown(textarea, { key: 'Enter' })
    expect(mutate).not.toHaveBeenCalled()
    fireEvent.keyDown(textarea, { key: 'Enter', metaKey: true })
    fireEvent.keyDown(textarea, { key: 'Enter', metaKey: true, repeat: true })
    expect(mutate).toHaveBeenCalledTimes(1)
    expect(screen.getByText('Command+Enter')).toBeVisible()
  })

  it('does not submit blank or pending comments', () => {
    const mutate = vi.fn()
    const pendingMutation = { error: null, isPending: true, mutate } as never
    render(
      <IssueCommentsSection
        comments={[]}
        issueNumber={455}
        issueProjectId="project-1"
        commentText="   "
        setCommentText={vi.fn()}
        deletingCommentId={null}
        setDeletingCommentId={vi.fn()}
        deleteCommentError={null}
        setDeleteCommentError={vi.fn()}
        mutations={{ addCommentMutation: pendingMutation, deleteCommentMutation: pendingMutation }}
      />,
    )
    fireEvent.keyDown(screen.getByPlaceholderText('Add a comment...'), { key: 'Enter', metaKey: true })
    expect(mutate).not.toHaveBeenCalled()
  })
})
