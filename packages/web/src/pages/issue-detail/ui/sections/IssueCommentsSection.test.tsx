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
    expect(mutate).toHaveBeenCalledWith({ displayName: 'Ada Lovelace', body: 'A useful comment' })
    expect(screen.getByText('Command+Enter')).toBeVisible()
  })

  it('requires a body before button or keyboard submission; the display alias is optional', () => {
    const { mutate } = renderSection({ commentText: '   ' })

    expect(screen.getByRole('button', { name: 'Comment' })).toBeDisabled()
    fireEvent.keyDown(screen.getByPlaceholderText('Add a comment...'), { key: 'Enter', metaKey: true })
    expect(mutate).not.toHaveBeenCalled()
  })

  it('submits without a display alias when the body is present', () => {
    const { mutate } = renderSection({ commentAuthor: '   ' })

    expect(screen.getByRole('button', { name: 'Comment' })).toBeEnabled()
    fireEvent.click(screen.getByRole('button', { name: 'Comment' }))
    expect(mutate).toHaveBeenCalledWith({ displayName: '   ', body: 'A useful comment' })
  })

  it('exposes an optional bounded display-name input', () => {
    const setCommentAuthor = vi.fn()
    renderSection({ setCommentAuthor })

    const input = screen.getByRole('textbox', { name: 'Display name' })
    expect(input).not.toBeRequired()
    expect(input).toHaveAttribute('maxlength', '100')
    fireEvent.change(input, { target: { value: 'New author' } })
    expect(setCommentAuthor).toHaveBeenCalledWith('New author')
  })

  it('renders the empty-comment submit button with an unmistakable disabled affordance', () => {
    renderSection({ commentText: '   ' })

    const submit = screen.getByRole('button', { name: 'Comment' })
    expect(submit).toBeDisabled()
    expect(submit.className).toMatch(/\bcursor-not-allowed\b/)
    expect(submit.className).toMatch(/\bbg-muted\b/)
    expect(submit.className).toMatch(/\btext-muted-foreground\b/)
    expect(submit.className).not.toMatch(/\bopacity-50\b/)
  })

  it('renders the in-flight delete-comment button with an unmistakable disabled affordance', () => {
    renderSection({
      comments: [
        { id: 'cmt-1', author: 'Ada', body: 'First body', createdAt: '2026-07-21T08:00:00Z' },
      ],
      deletingCommentId: 'cmt-1',
    })

    const deleteButton = screen.getByTestId('comment-delete-button')
    expect(deleteButton).toBeDisabled()
    expect(deleteButton.className).toMatch(/\bcursor-not-allowed\b/)
    expect(deleteButton.className).toMatch(/\bbg-muted\b/)
    expect(deleteButton.className).toMatch(/\btext-muted-foreground\b/)
    expect(deleteButton.className).not.toMatch(/\bopacity-50\b/)
  })
})
