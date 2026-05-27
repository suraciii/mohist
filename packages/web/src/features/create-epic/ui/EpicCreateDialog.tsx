import { useState } from 'react'
import { useCreateEpic } from '../../../entities/epic'
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
  const [title, setTitle] = useState('')
  const [description, setDescription] = useState('')
  const [priority, setPriority] = useState<EpicPriority>('p2')
  const createEpic = useCreateEpic()

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!title.trim()) return

    createEpic.mutate(
      { title: title.trim(), description, priority },
      {
        onSuccess: () => {
          setTitle('')
          setDescription('')
          setPriority('p2')
          onClose()
        },
      }
    )
  }

  function handleClose() {
    setTitle('')
    setDescription('')
    setPriority('p2')
    onClose()
  }

  return (
    <Dialog open={open} onOpenChange={(v) => !v && handleClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Create Epic</DialogTitle>
        </DialogHeader>
        <form onSubmit={handleSubmit} className="space-y-4">
          <div>
            <label htmlFor="epic-title" className="block text-sm font-medium text-foreground mb-1">
              Title
            </label>
            <Input
              id="epic-title"
              type="text"
              value={title}
              onChange={e => setTitle(e.target.value)}
              placeholder="e.g., Workflow runtime model cleanup"
              required
            />
          </div>

          <div>
            <label htmlFor="epic-description" className="block text-sm font-medium text-foreground mb-1">
              Description
            </label>
            <Textarea
              id="epic-description"
              value={description}
              onChange={e => setDescription(e.target.value)}
              placeholder="Describe the goal and scope of this epic..."
              rows={4}
              required
            />
          </div>

          <div>
            <label htmlFor="epic-priority" className="block text-sm font-medium text-foreground mb-1">
              Priority
            </label>
            <Select value={priority} onValueChange={(value) => value && setPriority(value as EpicPriority)}>
              <SelectTrigger id="epic-priority" className="w-full">
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

          {createEpic.isError && (
            <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
              {createEpic.error?.message || 'Failed to create epic'}
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
              disabled={createEpic.isPending || !title.trim()}
            >
              {createEpic.isPending ? 'Creating...' : 'Create Epic'}
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  )
}
