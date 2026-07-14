import { useEffect, useState } from 'react'
import { toast } from 'sonner'
import { useUpdateRunnerSlots } from '../../../entities/runner'

interface SlotsEditorProps {
  runnerId: string
  value: number
  mutationHook?: SlotsEditorMutationHook
}

export type SlotsEditorMutationHook = typeof useUpdateRunnerSlots

export function SlotsEditor({
  runnerId,
  value,
  mutationHook = useUpdateRunnerSlots,
}: SlotsEditorProps) {
  const [local, setLocal] = useState(value)
  const [committed, setCommitted] = useState(value)
  const mutation = mutationHook()

  useEffect(() => {
    setLocal(value)
    setCommitted(value)
  }, [value])

  const dirty = local !== committed

  function save(slots: number) {
    if (slots < 1) return
    mutation.mutate(
      { runnerId, slots },
      {
        onSuccess: (data) => {
          setCommitted(data.slots)
          setLocal(data.slots)
        },
        onError: (err) => {
          toast.error(`Failed to update slots: ${err instanceof Error ? err.message : 'Unknown error'}`)
          setLocal(committed)
        },
      },
    )
  }

  function handleChange(next: number) {
    const clamped = Math.max(1, next)
    setLocal(clamped)
  }

  function handleBlur() {
    if (dirty) save(local)
  }

  function handleKeyDown(e: React.KeyboardEvent) {
    if (e.key === 'Enter') {
      (e.target as HTMLInputElement).blur()
    }
  }

  const atMin = local <= 1

  return (
    <div className="inline-flex items-center gap-1" data-testid="slots-editor">
      <button
        type="button"
        className="inline-flex items-center justify-center w-6 h-6 rounded border border-border text-muted-foreground hover:bg-muted disabled:opacity-30 disabled:cursor-not-allowed text-sm leading-none"
        disabled={atMin || mutation.isPending}
        onClick={() => save(local - 1)}
        aria-label="Decrease slots"
        data-testid="slots-editor-decrease"
      >
        −
      </button>
      <input
        type="number"
        className="w-12 h-6 text-center text-xs border border-border rounded tabular-nums focus:outline-none focus:border-info-border focus:ring-1 focus:ring-info-border [appearance:textfield] [&::-webkit-outer-spin-button]:appearance-none [&::-webkit-inner-spin-button]:appearance-none"
        min={1}
        value={local}
        onChange={(e) => handleChange(Number(e.target.value))}
        onBlur={handleBlur}
        onKeyDown={handleKeyDown}
        disabled={mutation.isPending}
        data-testid="slots-editor-input"
      />
      <button
        type="button"
        className="inline-flex items-center justify-center w-6 h-6 rounded border border-border text-muted-foreground hover:bg-muted disabled:opacity-30 disabled:cursor-not-allowed text-sm leading-none"
        disabled={mutation.isPending}
        onClick={() => save(local + 1)}
        aria-label="Increase slots"
        data-testid="slots-editor-increase"
      >
        +
      </button>
      {mutation.isPending && (
        <span className="text-xs text-muted-foreground ml-1" data-testid="slots-editor-saving">
          saving…
        </span>
      )}
    </div>
  )
}
