import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useCreateEpic, type Epic } from '../../../entities/epic'
import { useProject, useProjectPath } from '../../../entities/project'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogFooter,
} from '@/shared/ui/components/dialog'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/shared/ui/components/select'
import { EpicDescriptionField } from '@/shared/ui'
import { cn } from '@/shared/lib/utils'
import { EPIC_DESCRIPTION_TEMPLATE, hasEpicDescriptionStructure } from '@/shared/lib/epic-description-template'
import type { EpicPriority } from '../../../entities/epic'

interface EpicCreateDialogProps {
  open: boolean
  onClose: () => void
}

const PRIORITIES: { value: EpicPriority; label: string }[] = [
  { value: 'p0', label: 'P0 - Critical' },
  { value: 'p1', label: 'P1 - High' },
  { value: 'p2', label: 'P2 - Medium' },
  { value: 'p3', label: 'P3 - Low' },
  { value: 'p4', label: 'P4 - Nice to have' },
]

export function EpicCreateDialog({ open, onClose }: EpicCreateDialogProps) {
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  const { projectId } = useProject()
  const [title, setTitle] = useState('')
  const [description, setDescription] = useState(EPIC_DESCRIPTION_TEMPLATE)
  const [priority, setPriority] = useState<EpicPriority>('p2')
  const [createdEpic, setCreatedEpic] = useState<Epic | null>(null)
  const createEpic = useCreateEpic()

  function resetForm() {
    setTitle('')
    setDescription(EPIC_DESCRIPTION_TEMPLATE)
    setPriority('p2')
    setCreatedEpic(null)
  }

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!title.trim()) return

    createEpic.mutate(
      { title: title.trim(), description, priority },
      {
        onSuccess: (data) => {
          setTitle('')
          setDescription(EPIC_DESCRIPTION_TEMPLATE)
          setPriority('p2')
          setCreatedEpic(data)
        },
      },
    )
  }

  function handleClose() {
    resetForm()
    onClose()
  }

  function handleOpenEpic() {
    if (!createdEpic) {
      onClose()
      return
    }
    setCreatedEpic(null)
    onClose()
    navigate(toProjectPath(`/epics/${createdEpic.number}`))
  }

  function handleStay() {
    handleClose()
  }

  const isSuccess = createdEpic != null
  const showInsertAction = !hasEpicDescriptionStructure(description)

  return (
    <Dialog open={open} onOpenChange={(value) => !value && handleClose()}>
      <DialogContent
        data-testid="epic-create-dialog"
        className={cn(
          'flex max-h-[calc(100dvh-2rem)] flex-col gap-0 overflow-hidden p-0 sm:max-w-lg',
          'max-w-[calc(100%-2rem)]',
        )}
      >
        <DialogHeader className="border-b border-foreground/10 px-4 py-3">
          <DialogTitle>Create Epic</DialogTitle>
        </DialogHeader>

        <div className="min-h-0 flex-1 overflow-y-auto px-4 py-3" data-testid="epic-create-scroll-region">
          {isSuccess && createdEpic ? (
            <div className="space-y-3" data-testid="epic-create-success">
              <div className="space-y-1">
                <p className="text-sm font-medium text-foreground" data-testid="epic-create-success-title">
                  Epic created as idle — ready to plan
                </p>
                <p className="text-sm text-muted-foreground break-words">
                  <span className="font-medium text-foreground">{createdEpic.title}</span>
                  {createdEpic.number != null ? (
                    <span className="ml-1">#{createdEpic.number}</span>
                  ) : null}
                  {' '}is idle. Start the Epic when you want it to begin autonomous execution.
                </p>
              </div>
              <p className="text-xs text-muted-foreground">
                Use <span className="font-medium text-foreground">Open Epic</span> to continue planning linked issues, or{' '}
                <span className="font-medium text-foreground">Stay</span> on this page.
              </p>
            </div>
          ) : (
            <form
              id="epic-create-form"
              onSubmit={handleSubmit}
              className="space-y-4"
              data-testid="epic-create-form"
            >
              <div>
                <label
                  htmlFor="epic-title"
                  className="block text-sm font-medium text-foreground mb-1"
                >
                  Title
                </label>
                <Input
                  id="epic-title"
                  type="text"
                  value={title}
                  onChange={(e) => setTitle(e.target.value)}
                  placeholder="e.g., Workflow runtime model cleanup"
                  required
                  className="w-full max-w-full"
                />
              </div>

              <EpicDescriptionField
                id="epic-description"
                value={description}
                onChange={setDescription}
                showInsertAction={showInsertAction}
                rows={6}
              />

              <div>
                <label
                  htmlFor="epic-priority"
                  className="block text-sm font-medium text-foreground mb-1"
                >
                  Priority
                </label>
                <Select
                  value={priority}
                  onValueChange={(value) => value && setPriority(value as EpicPriority)}
                >
                  <SelectTrigger id="epic-priority" className="w-full max-w-full">
                    <SelectValue placeholder="Select priority" />
                  </SelectTrigger>
                  <SelectContent>
                    {PRIORITIES.map((p) => (
                      <SelectItem key={p.value} value={p.value}>
                        {p.label}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>

              {createEpic.isError && (
                <div
                  className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600"
                  data-testid="epic-create-error"
                >
                  {createEpic.error?.message || 'Failed to create epic'}
                </div>
              )}
            </form>
          )}
        </div>

        <DialogFooter
          className="mx-0 mb-0 border-t border-foreground/10 bg-muted/30 px-4 py-3"
          data-testid="epic-create-footer"
        >
          {isSuccess ? (
            <>
              <Button
                type="button"
                variant="outline"
                onClick={handleStay}
                data-testid="epic-create-stay"
              >
                Stay
              </Button>
              <Button
                type="button"
                onClick={handleOpenEpic}
                data-testid="epic-create-open"
              >
                Open Epic
              </Button>
            </>
          ) : (
            <>
              <Button
                type="button"
                variant="outline"
                onClick={handleClose}
                data-testid="epic-create-cancel"
              >
                Cancel
              </Button>
              <Button
                type="submit"
                form="epic-create-form"
                disabled={createEpic.isPending || !title.trim()}
                data-testid="epic-create-submit"
              >
                {createEpic.isPending ? 'Creating…' : 'Create Epic'}
              </Button>
            </>
          )}
        </DialogFooter>

        {!projectId ? (
          <span className="sr-only" data-testid="epic-create-no-project">No project selected.</span>
        ) : null}
      </DialogContent>
    </Dialog>
  )
}
