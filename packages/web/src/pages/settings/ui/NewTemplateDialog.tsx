import { useState } from 'react'
import { AlertTriangleIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogTitle,
} from '@/shared/ui/components/dialog'
import { useUpsertProjectTemplateOverride } from '../../../entities/template'

interface Props {
  open: boolean
  projectId: string
  onClose: () => void
}

export function NewTemplateDialog({ open, projectId, onClose }: Props) {
  const upsert = useUpsertProjectTemplateOverride(projectId)
  const [key, setKey] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [body, setBody] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [keyTouched, setKeyTouched] = useState(false)

  function reset() {
    setKey('')
    setDisplayName('')
    setBody('')
    setError(null)
    setKeyTouched(false)
  }

  function handleClose() {
    reset()
    onClose()
  }

  function handleCreate() {
    if (!key.trim()) {
      setError('Key is required')
      return
    }
    if (!body.trim()) {
      setError('Body is required')
      return
    }
    setError(null)
    upsert.mutate(
      {
        key: key.trim(),
        payload: {
          displayName: displayName.trim() || key.trim(),
          description: '',
          tags: [],
          stage: null,
          body,
        },
      },
      {
        onSuccess: () => {
          reset()
          onClose()
        },
      },
    )
  }

  return (
    <Dialog open={open} onOpenChange={(o) => !o && handleClose()}>
      <DialogContent
        className="sm:max-w-md"
        data-testid="new-template-dialog"
        showCloseButton={false}
      >
        <DialogTitle>New Template</DialogTitle>
        <DialogDescription>
          Create a project-unique template. Renaming later requires delete + create and may break
          workflow YAML references.
        </DialogDescription>
        <div className="space-y-2">
          <div>
            <label className="block text-xs font-medium text-foreground/80">Key</label>
            <Input
              value={key}
              onChange={(e) => {
                setKey(e.target.value)
                if (!keyTouched) setKeyTouched(true)
              }}
              data-testid="new-template-key"
              placeholder="deploy-checklist"
              className="h-8 text-sm font-mono"
            />
            {keyTouched && (
              <div
                data-testid="new-template-key-warning"
                className="mt-1 flex items-start gap-1.5 rounded-md border border-amber-200 bg-amber-50 px-2 py-1.5 text-[11px] text-amber-800"
              >
                <AlertTriangleIcon className="mt-0.5 size-3 shrink-0" />
                <span>
                  Choose this key carefully. Renaming later requires delete + create and will break
                  any workflow YAML that references the old key.
                </span>
              </div>
            )}
          </div>
          <div>
            <label className="block text-xs font-medium text-foreground/80">Display Name</label>
            <Input
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
              data-testid="new-template-displayname"
              placeholder="Deploy Checklist"
              className="h-8 text-sm"
            />
          </div>
          <div>
            <label className="block text-xs font-medium text-foreground/80">Body</label>
            <textarea
              value={body}
              onChange={(e) => setBody(e.target.value)}
              data-testid="new-template-body"
              placeholder="Initial body content"
              className="min-h-[120px] w-full rounded-lg border border-input bg-transparent px-2.5 py-1.5 font-mono text-xs outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50"
            />
          </div>
          {error && (
            <p data-testid="new-template-error" className="text-xs text-red-600">
              {error}
            </p>
          )}
        </div>
        <div className="flex justify-end gap-2">
          <DialogClose
            render={<Button variant="ghost" size="sm" data-testid="new-template-cancel" />}
            onClick={handleClose}
          >
            Cancel
          </DialogClose>
          <Button
            size="sm"
            disabled={upsert.isPending}
            onClick={handleCreate}
            data-testid="new-template-create"
          >
            {upsert.isPending ? 'Creating...' : 'Create'}
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}
