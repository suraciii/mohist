import { useEffect, useState } from 'react'
import { useUpdateEpic, type EpicDetail } from '../../../entities/epic'
import type { EpicPriority } from '../../../entities/epic'
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

interface EditEpicDialogProps {
  open: boolean
  onClose: () => void
  epic: EpicDetail
  updateHook?: typeof useUpdateEpic
}

const PRIORITIES: { value: EpicPriority; label: string }[] = [
  { value: 'p0', label: 'P0 - Critical' },
  { value: 'p1', label: 'P1 - High' },
  { value: 'p2', label: 'P2 - Medium' },
  { value: 'p3', label: 'P3 - Low' },
  { value: 'p4', label: 'P4 - Nice to have' },
]

export function EditEpicDialog({
  open,
  onClose,
  epic,
  updateHook = useUpdateEpic,
}: EditEpicDialogProps) {
  const [title, setTitle] = useState(epic.title)
  const [description, setDescription] = useState(epic.description)
  const [priority, setPriority] = useState<EpicPriority>((epic.priority as EpicPriority) || 'p2')
  const updateEpic = updateHook()

  useEffect(() => {
    if (open) {
      setTitle(epic.title)
      setDescription(epic.description)
      setPriority((epic.priority as EpicPriority) || 'p2')
    }
  }, [open, epic])

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!title.trim()) return

    updateEpic.mutate(
      {
        number: epic.number,
        data: {
          title: title.trim(),
          description,
          priority,
        },
      },
      { onSuccess: () => onClose() },
    )
  }

  function handleClose() {
    onClose()
  }

  return (
    <Dialog open={open} onOpenChange={(v) => !v && handleClose()}>
      <DialogContent
        data-testid="edit-epic-dialog"
        className={cn(
          'flex max-h-[calc(100dvh-2rem)] flex-col gap-0 overflow-hidden p-0 sm:max-w-lg',
          'max-w-[calc(100%-2rem)]',
        )}
      >
        <DialogHeader className="border-b border-foreground/10 px-4 py-3">
          <DialogTitle>Edit Epic {epic.number != null ? `#${epic.number}` : ''}</DialogTitle>
        </DialogHeader>

        <div className="min-h-0 flex-1 overflow-y-auto px-4 py-3" data-testid="edit-epic-scroll-region">
          <form
            id="edit-epic-form"
            onSubmit={handleSubmit}
            className="space-y-4"
            data-testid="edit-epic-form"
          >
            <div>
              <label
                htmlFor="edit-epic-title"
                className="block text-sm font-medium text-foreground mb-1"
              >
                Title
              </label>
              <Input
                id="edit-epic-title"
                type="text"
                value={title}
                onChange={(e) => setTitle(e.target.value)}
                placeholder="Epic title"
                required
                className="w-full max-w-full"
              />
            </div>

            <EpicDescriptionField
              id="edit-epic-description"
              value={description}
              onChange={setDescription}
              showInsertAction
              rows={6}
            />

            <div>
              <label
                htmlFor="edit-epic-priority"
                className="block text-sm font-medium text-foreground mb-1"
              >
                Priority
              </label>
              <Select value={priority} onValueChange={(value) => value && setPriority(value as EpicPriority)}>
                <SelectTrigger id="edit-epic-priority" className="w-full max-w-full">
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

            {updateEpic.isError && (
              <div
                className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600"
                data-testid="edit-epic-error"
              >
                {updateEpic.error?.message || 'Failed to update epic'}
              </div>
            )}
          </form>
        </div>

        <DialogFooter
          className="mx-0 mb-0 border-t border-foreground/10 bg-muted/30 px-4 py-3"
          data-testid="edit-epic-footer"
        >
          <Button
            type="button"
            variant="outline"
            onClick={handleClose}
            data-testid="edit-epic-cancel"
          >
            Cancel
          </Button>
          <Button
            type="submit"
            form="edit-epic-form"
            disabled={updateEpic.isPending || !title.trim()}
            data-testid="edit-epic-submit"
          >
            {updateEpic.isPending ? 'Saving...' : 'Save'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
