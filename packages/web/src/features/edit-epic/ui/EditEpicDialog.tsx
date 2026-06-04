import { useEffect, useState } from 'react'
import { useUpdateEpic, type EpicDetail } from '../../../entities/epic'
import type { EpicPriority } from '../../../entities/epic'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '@/shared/ui/components/dialog'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { Textarea } from '@/shared/ui/components/textarea'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/shared/ui/components/select'

interface EditEpicDialogProps {
  open: boolean
  onClose: () => void
  epic: EpicDetail
}

const PRIORITIES: { value: EpicPriority; label: string }[] = [
  { value: 'p0', label: 'P0 - Critical' },
  { value: 'p1', label: 'P1 - High' },
  { value: 'p2', label: 'P2 - Medium' },
  { value: 'p3', label: 'P3 - Low' },
  { value: 'p4', label: 'P4 - Nice to have' },
]

export function EditEpicDialog({ open, onClose, epic }: EditEpicDialogProps) {
  const [title, setTitle] = useState(epic.title)
  const [description, setDescription] = useState(epic.description)
  const [priority, setPriority] = useState<EpicPriority>((epic.priority as EpicPriority) || 'p2')
  const updateEpic = useUpdateEpic()

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
        id: epic.id,
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
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Edit Epic {epic.number != null ? `#${epic.number}` : ''}</DialogTitle>
        </DialogHeader>
        <form onSubmit={handleSubmit} className="space-y-4">
          <div>
            <label htmlFor="edit-epic-title" className="block text-sm font-medium text-foreground mb-1">
              Title
            </label>
            <Input
              id="edit-epic-title"
              type="text"
              value={title}
              onChange={e => setTitle(e.target.value)}
              placeholder="Epic title"
              required
            />
          </div>

          <div>
            <label htmlFor="edit-epic-description" className="block text-sm font-medium text-foreground mb-1">
              Description
            </label>
            <Textarea
              id="edit-epic-description"
              value={description}
              onChange={e => setDescription(e.target.value)}
              placeholder="Describe the goal and scope of this epic..."
              rows={4}
            />
          </div>

          <div>
            <label htmlFor="edit-epic-priority" className="block text-sm font-medium text-foreground mb-1">
              Priority
            </label>
            <Select value={priority} onValueChange={(value) => value && setPriority(value as EpicPriority)}>
              <SelectTrigger id="edit-epic-priority" className="w-full">
                <SelectValue placeholder="Select priority" />
              </SelectTrigger>
              <SelectContent>
                {PRIORITIES.map(p => (
                  <SelectItem key={p.value} value={p.value}>
                    {p.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>

          {updateEpic.isError && (
            <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
              {updateEpic.error?.message || 'Failed to update epic'}
            </div>
          )}

          <div className="flex justify-end gap-2 pt-2">
            <Button
              type="button"
              variant="outline"
              onClick={handleClose}
            >
              Cancel
            </Button>
            <Button
              type="submit"
              disabled={updateEpic.isPending || !title.trim()}
            >
              {updateEpic.isPending ? 'Saving...' : 'Save'}
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  )
}
