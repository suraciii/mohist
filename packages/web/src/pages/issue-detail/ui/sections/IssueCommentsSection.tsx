import { useState, type KeyboardEvent } from 'react'
import { AlertDialog } from '@/shared/ui/components/alert-dialog'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { AttachmentComposer, MarkdownReader } from '@/shared/ui'
import { commentAttachmentContentPath, type Comment } from '../../../../entities/issue'
import { formatTime } from '../../../../shared/lib/format-time'
import { attachmentFromMetadata } from '../../model/format'
import type { IssueDetailMutations } from '../../model/useIssueDetailMutations'

export interface IssueCommentsSectionProps {
  comments: Comment[]
  issueNumber: number
  issueProjectId: string
  commentText: string
  setCommentText: (value: string) => void
  commentAuthor: string
  setCommentAuthor: (value: string) => void
  deletingCommentId: string | null
  setDeletingCommentId: (value: string | null) => void
  deleteCommentError: string | null
  setDeleteCommentError: (value: string | null) => void
  mutations: Pick<IssueDetailMutations, 'addCommentMutation' | 'deleteCommentMutation'>
}

export function IssueCommentsSection({
  comments,
  issueNumber,
  issueProjectId,
  commentText,
  setCommentText,
  commentAuthor,
  setCommentAuthor,
  deletingCommentId,
  setDeletingCommentId,
  deleteCommentError,
  setDeleteCommentError,
  mutations,
}: IssueCommentsSectionProps) {
  const { addCommentMutation, deleteCommentMutation } = mutations
  const [pendingDeleteCommentId, setPendingDeleteCommentId] = useState<string | null>(null)
  const canSubmit = !!commentText.trim() && !addCommentMutation.isPending
  const submitComment = () => {
    if (!canSubmit) return
    addCommentMutation.mutate({ displayName: commentAuthor, body: commentText })
  }
  const handleCommentKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key !== 'Enter' || !event.metaKey || event.repeat || event.nativeEvent.isComposing) return
    event.preventDefault()
    submitComment()
  }

  return (
    <section
      id="comments"
      data-testid="comments-section"
      className="scroll-mt-20"
      data-tier-weight="reading-flow"
      aria-label="Issue comments"
    >
      <h2 className="text-sm font-semibold text-foreground mb-3">
        Comments ({comments.length})
      </h2>
      {comments.length === 0 ? (
        <p className="text-sm text-muted-foreground">No comments yet.</p>
      ) : (
        <div className="space-y-4">
          {comments.map((comment) => (
            <div
              key={comment.id}
              data-testid="issue-comment"
              className="border-b border-border/40 pb-3 last:border-0 last:pb-0"
            >
              <div className="flex items-start justify-between gap-2">
                <div className="flex-1 min-w-0">
                  <div
                    data-testid="comment-metadata"
                    className="mb-1 flex min-w-0 flex-wrap items-baseline gap-x-2 gap-y-0.5 text-xs text-muted-foreground"
                  >
                    <span className="min-w-0 break-words font-medium text-foreground/80">
                      {comment.displayName?.trim() || comment.author?.trim() || 'Unknown author'}
                    </span>
                    <time dateTime={comment.createdAt}>{formatTime(comment.createdAt)}</time>
                  </div>
                  <MarkdownReader
                    content={comment.body}
                    baseHeadingLevel={3}
                    resolveAttachment={(id) => attachmentFromMetadata(
                      id,
                      comment.attachments,
                      `/api${commentAttachmentContentPath(issueNumber, comment.id, id, issueProjectId)}`,
                    )}
                  />
                </div>
                <Button
                  variant="ghost"
                  size="xs"
                  onClick={() => {
                    setDeleteCommentError(null)
                    setPendingDeleteCommentId(comment.id)
                  }}
                  disabled={deletingCommentId === comment.id}
                  className="text-muted-foreground hover:text-danger"
                  title="Delete comment"
                  data-testid="comment-delete-button"
                >
                  {deletingCommentId === comment.id ? 'Deleting...' : 'Delete'}
                </Button>
              </div>
              {deleteCommentError && deletingCommentId === null && pendingDeleteCommentId === null && (
                <div className="mt-1 text-xs text-danger" data-testid="comment-delete-error">
                  {deleteCommentError}
                </div>
              )}
            </div>
          ))}
        </div>
      )}

      <div className="mt-5 pt-3 border-t border-border/40">
        <label className="mb-2 block text-xs font-medium text-foreground" htmlFor="comment-author">
          Display name
        </label>
        <Input
          id="comment-author"
          value={commentAuthor}
          onChange={(event) => setCommentAuthor(event.target.value)}
          maxLength={100}
          autoComplete="name"
          className="mb-2"
        />
        <AttachmentComposer
          projectId={issueProjectId}
          value={commentText}
          onChange={setCommentText}
          placeholder="Add a comment..."
          rows={2}
          className="resize-none"
          onKeyDown={handleCommentKeyDown}
        />
        <div className="flex items-center justify-between mt-2">
          {addCommentMutation.error && (
            <span className="text-xs text-danger">
              {addCommentMutation.error.message}
            </span>
          )}
          <div className="ml-auto">
            <Button
              onClick={submitComment}
              disabled={!canSubmit}
            >
              {addCommentMutation.isPending ? 'Sending...' : 'Comment'}
            </Button>
            <div className="mt-1 text-right text-xs text-muted-foreground">Use <kbd className="rounded border border-border bg-card px-1 py-0.5 font-mono">Command+Enter</kbd></div>
          </div>
        </div>
      </div>

      <AlertDialog
        open={pendingDeleteCommentId !== null}
        onOpenChange={(open) => {
          if (!open && deletingCommentId === null) {
            setPendingDeleteCommentId(null)
          }
        }}
        title="Delete this comment?"
        description="This comment will be permanently removed. This action cannot be undone."
        confirmLabel={deletingCommentId !== null ? 'Deleting...' : 'Delete'}
        cancelLabel="Cancel"
        tone="destructive"
        loading={deletingCommentId !== null}
        onConfirm={() => {
          if (pendingDeleteCommentId !== null) {
            setDeletingCommentId(pendingDeleteCommentId)
            deleteCommentMutation.mutate(pendingDeleteCommentId)
          }
        }}
        data-testid="comment-delete-alert"
      />
    </section>
  )
}
