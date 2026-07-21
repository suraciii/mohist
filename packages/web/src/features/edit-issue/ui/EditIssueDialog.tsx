import { useState, useEffect, useMemo } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '@/shared/ui/components/dialog'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { AttachmentComposer } from '@/shared/ui'
import { extractAttachmentIds, LabelEditor, partitionIssueBody, recombineIssueBody, updateIssue } from '../../../entities/issue'
import type { Issue, LabelMap } from '../../../entities/issue'
import { getPriorityStyle } from '../../../shared/lib/label-colors'

const PRIORITIES = ['p0', 'p1', 'p2', 'p3', 'p4']

interface Props {
  open: boolean
  onClose: () => void
  issue: Issue
  issueUpdater?: typeof updateIssue
}

export function EditIssueDialog({
  open,
  onClose,
  issue,
  issueUpdater = updateIssue,
}: Props) {
  const bodyPartition = useMemo(() => partitionIssueBody(issue.body), [issue.body])
  const [title, setTitle] = useState(issue.title)
  const [description, setDescription] = useState(bodyPartition.description)
  const [labels, setLabels] = useState<LabelMap>(issue.labels ?? {})
  const [priority, setPriority] = useState<string>(issue.priority ?? 'p2')
  const [isDraft, setIsDraft] = useState(issue.isDraft)
  const queryClient = useQueryClient()

  useEffect(() => {
    if (open) {
      setTitle(issue.title)
      setDescription(bodyPartition.description)
      setLabels(issue.labels ?? {})
      setPriority(issue.priority ?? 'p2')
      setIsDraft(issue.isDraft)
    }
  }, [bodyPartition.description, open, issue])

  const mutation = useMutation({
    mutationFn: () => {
      const body = recombineIssueBody(bodyPartition, description)
      return issueUpdater(issue.number, {
        title,
        body: body || undefined,
        attachmentIds: extractAttachmentIds(body),
        labels,
        priority,
        isDraft,
      }, issue.projectId)
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      onClose()
    },
  })

  return (
    <Dialog open={open} onOpenChange={(v) => !v && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Edit Issue #{issue.number}</DialogTitle>
        </DialogHeader>
        <div className="space-y-3">
          <div>
            <label className="block text-xs font-medium text-foreground mb-1">Title</label>
            <Input
              type="text"
              value={title}
              onChange={(e) => setTitle(e.target.value)}
              autoFocus
            />
          </div>

          <div>
            <label className="block text-xs font-medium text-foreground mb-1">Description</label>
            <AttachmentComposer
              projectId={issue.projectId}
              value={description}
              onChange={setDescription}
              placeholder="Optional description"
              rows={4}
              className="resize-none"
            />
          </div>

          <div>
            <label className="block text-xs font-medium text-foreground mb-1">Labels</label>
            <LabelEditor
              value={labels}
              onChange={setLabels}
              inputIdPrefix="edit-issue-label"
              emptyHint="No labels yet — add a key+value pair to classify this issue."
            />
          </div>

          <div>
            <label className="block text-xs font-medium text-foreground mb-1">Priority</label>
            <div className="flex gap-1.5">
              {PRIORITIES.map((p) => {
                const style = getPriorityStyle(p)
                return (
                  <Button
                    key={p}
                    type="button"
                    variant="ghost"
                    size="xs"
                    onClick={() => setPriority(p)}
                    className={`rounded-full ${
                      priority === p
                        ? 'ring-1 ring-offset-1'
                        : 'hover:opacity-80'
                    }`}
                    style={{
                      backgroundColor: style.bg,
                      color: style.text,
                      ...(priority === p ? { ringColor: style.text } : {}),
                    }}
                  >
                    {p.toUpperCase()}
                  </Button>
                )
              })}
            </div>
          </div>

          <div>
            <label className="block text-xs font-medium text-foreground mb-1">Status</label>
            <div className="flex gap-1.5">
              <Button
                type="button"
                variant="ghost"
                size="xs"
                onClick={() => setIsDraft(true)}
                className={`rounded-full ${
                  isDraft
                    ? 'ring-1 ring-offset-1 ring-muted-foreground bg-muted text-muted-foreground'
                    : 'hover:opacity-80 text-muted-foreground'
                }`}
              >
                Draft
              </Button>
              <Button
                type="button"
                variant="ghost"
                size="xs"
                onClick={() => setIsDraft(false)}
                className={`rounded-full ${
                  !isDraft
                    ? 'ring-1 ring-offset-1 ring-green-600 bg-green-100 text-green-700'
                    : 'hover:opacity-80 text-muted-foreground'
                }`}
              >
                Ready
              </Button>
            </div>
          </div>

          {mutation.error && (
            <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
              {mutation.error.message}
            </div>
          )}

          <div className="flex justify-end gap-2 pt-1">
            <Button
              variant="outline"
              onClick={onClose}
            >
              Cancel
            </Button>
            <Button
              onClick={() => mutation.mutate()}
              disabled={!title.trim() || mutation.isPending}
            >
              {mutation.isPending ? 'Saving...' : 'Save'}
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  )
}
