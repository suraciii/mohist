import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, within } from '@testing-library/react'
import { IssueCommentsSection, type IssueCommentsSectionProps } from './IssueCommentsSection'

function renderSection(overrides: Partial<IssueCommentsSectionProps> = {}) {
  const mutate = vi.fn()
  const mutation = { error: null, isPending: false, mutate } as never
  const props: IssueCommentsSectionProps = {
    comments: [],
    issueNumber: 455,
    issueProjectId: 'project-1',
    commentAuthor: 'Ada Lovelace',
    setCommentAuthor: vi.fn(),
    commentText: 'A useful comment',
    setCommentText: vi.fn(),
    deletingCommentId: null,
    setDeletingCommentId: vi.fn(),
    deleteCommentError: null,
    setDeleteCommentError: vi.fn(),
    mutations: { addCommentMutation: mutation, deleteCommentMutation: mutation },
    ...overrides,
  }
  return { ...render(<IssueCommentsSection {...props} />), mutate, props }
}

describe('IssueCommentsSection', () => {
  it('shows each recorded author and timestamp with its matching body', () => {
    renderSection({
      comments: [
        { id: 'cmt-1', author: 'Ada Lovelace', body: 'First body', createdAt: '2026-07-21T08:00:00Z' },
        { id: 'cmt-2', author: 'Grace Hopper', body: 'Second body', createdAt: '2026-07-21T09:00:00Z' },
      ],
    })

    const firstComment = screen.getByText('First body').closest<HTMLElement>('[class*="border-b"]')!
    expect(within(firstComment).getByText('Ada Lovelace')).toBeVisible()
    expect(firstComment.querySelector('time')).toHaveAttribute('datetime', '2026-07-21T08:00:00Z')
    const secondComment = screen.getByText('Second body').closest<HTMLElement>('[class*="border-b"]')!
    expect(within(secondComment).getByText('Grace Hopper')).toBeVisible()
    expect(secondComment.querySelector('time')).toHaveAttribute('datetime', '2026-07-21T09:00:00Z')
  })

  it.each([null, '', '   '])('renders Unknown author for historical author value %s', (author) => {
    renderSection({
      comments: [{ id: 'cmt-history', author, body: 'Historical body', createdAt: '2026-07-21T08:00:00Z' }],
    })

    expect(screen.getByText('Unknown author')).toBeVisible()
    expect(screen.getByText('Historical body')).toBeVisible()
  })

  it('uses one guarded submit callback for the button and Command+Enter', () => {
    const { mutate } = renderSection()

    const textarea = screen.getByPlaceholderText('Add a comment...')
    fireEvent.keyDown(textarea, { key: 'Enter' })
    expect(mutate).not.toHaveBeenCalled()
    fireEvent.keyDown(textarea, { key: 'Enter', metaKey: true })
    fireEvent.keyDown(textarea, { key: 'Enter', metaKey: true, repeat: true })
    expect(mutate).toHaveBeenCalledTimes(1)
    expect(mutate).toHaveBeenCalledWith({ author: 'Ada Lovelace', body: 'A useful comment' })
    expect(screen.getByText('Command+Enter')).toBeVisible()
  })

  it.each([
    { commentAuthor: '   ', commentText: 'A body' },
    { commentAuthor: 'Ada', commentText: '   ' },
  ])('requires both author and body before button or keyboard submission', ({ commentAuthor, commentText }) => {
    const { mutate } = renderSection({ commentAuthor, commentText })

    expect(screen.getByRole('button', { name: 'Comment' })).toBeDisabled()
    fireEvent.keyDown(screen.getByPlaceholderText('Add a comment...'), { key: 'Enter', metaKey: true })
    expect(mutate).not.toHaveBeenCalled()
  })

  it('exposes a required bounded Author input', () => {
    const setCommentAuthor = vi.fn()
    renderSection({ setCommentAuthor })

    const input = screen.getByRole('textbox', { name: 'Author' })
    expect(input).toBeRequired()
    expect(input).toHaveAttribute('maxlength', '100')
    fireEvent.change(input, { target: { value: 'New author' } })
    expect(setCommentAuthor).toHaveBeenCalledWith('New author')
  })
})
