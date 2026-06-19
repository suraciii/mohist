import { useState, useEffect } from 'react'
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
import { extractAttachmentIds, updateIssue } from '../../../entities/issue'
import { LabelEditor } from '../../../entities/issue/lib/label-editor'
import type { LabelMap } from '../../../entities/issue/model/labels'
import type { Issue } from '../../../entities/issue'
import { getPriorityStyle } from '../../../shared/lib/label-colors'

const PRIORITIES = ['p0', 'p1', 'p2', 'p3', 'p4']

interface Props {
  open: boolean
  onClose: () => void
  issue: Issue
}

export function EditIssueDialog({ open, onClose, issue }: Props) {
  const [title, setTitle] = useState(issue.title)
  const [body, setBody] = useState(issue.body ?? '')
  const [labels, setLabels] = useState<LabelMap>(issue.labels ?? {})
  const [priority, setPriority] = useState<string>(issue.priority ?? 'p2')
  const queryClient = useQueryClient()

  useEffect(() => {
    if (open) {
      setTitle(issue.title)
      setBody(issue.body ?? '')
      setLabels(issue.labels ?? {})
      setPriority(issue.priority ?? 'p2')
    }
  }, [open, issue])

  const mutation = useMutation({
    mutationFn: () =>
      updateIssue(issue.number, {
        title,
        body: body || undefined,
        attachmentIds: extractAttachmentIds(body),
        labels,
        priority,
      }, issue.projectId),
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
              value={body}
              onChange={setBody}
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