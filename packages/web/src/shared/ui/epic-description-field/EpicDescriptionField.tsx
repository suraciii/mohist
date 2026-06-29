import { Button } from '@/shared/ui/components/button'
import { Textarea } from '@/shared/ui/components/textarea'
import { cn } from '@/shared/lib/utils'
import { EPIC_DESCRIPTION_TEMPLATE } from '@/shared/lib/epic-description-template'

export interface EpicDescriptionFieldProps {
  id: string
  label?: string
  value: string
  onChange: (next: string) => void
  /** When true, renders an opt-in Insert-template action below the textarea. */
  showInsertAction?: boolean
  insertActionLabel?: string
  rows?: number
  placeholder?: string
  className?: string
  disabled?: boolean
}

/**
 * Shared free-form markdown description field for the Create/Edit Epic
 * dialogs. Renders a label, a `<Textarea>`, and (optionally) an opt-in
 * "Insert template" action that appends the Epic description scaffold
 * without destroying existing user text.
 *
 * The wrapper applies mobile-safe width classes (`w-full max-w-full`) and
 * `break-words` so long content wraps rather than overflowing on small
 * viewports (320/390/430 px).
 */
export function EpicDescriptionField({
  id,
  label = 'Description',
  value,
  onChange,
  showInsertAction = false,
  insertActionLabel = 'Insert template',
  rows = 6,
  placeholder = 'Describe the goal and scope of this epic…',
  className,
  disabled = false,
}: EpicDescriptionFieldProps) {
  function handleInsertTemplate() {
    if (value.length === 0) {
      onChange(EPIC_DESCRIPTION_TEMPLATE)
      return
    }
    let separator: string
    if (value.endsWith('\n\n')) {
      separator = ''
    } else if (value.endsWith('\n')) {
      separator = '\n'
    } else {
      separator = '\n\n'
    }
    onChange(`${value}${separator}${EPIC_DESCRIPTION_TEMPLATE}`)
  }

  return (
    <div className={cn('w-full max-w-full break-words', className)}>
      <label
        htmlFor={id}
        className="block text-sm font-medium text-foreground mb-1"
      >
        {label}
      </label>
      <Textarea
        id={id}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        placeholder={placeholder}
        rows={rows}
        disabled={disabled}
      />
      {showInsertAction && (
        <div className="mt-1 flex justify-end">
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={handleInsertTemplate}
            disabled={disabled}
          >
            {insertActionLabel}
          </Button>
        </div>
      )}
    </div>
  )
}
