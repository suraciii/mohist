import { APPROVAL_OPERATOR_MAX_LENGTH } from '../../../entities/issue'
import { Input } from '@/shared/ui/components/input'
import { cn } from '@/shared/lib/utils'

export interface ApprovalOperatorFieldProps {
  id: string
  value: string
  onChange(value: string): void
  disabled?: boolean
  className?: string
  testId?: string
}

export function ApprovalOperatorField({
  id,
  value,
  onChange,
  disabled = false,
  className,
  testId = 'approval-operator-input',
}: ApprovalOperatorFieldProps) {
  return (
    <div className={cn('space-y-1.5', className)}>
      <label htmlFor={id} className="block text-xs font-medium text-foreground">
        Approval operator
      </label>
      <Input
        id={id}
        name="approvalOperator"
        value={value}
        onChange={(event) => onChange(event.target.value)}
        disabled={disabled}
        required
        maxLength={APPROVAL_OPERATOR_MAX_LENGTH}
        autoComplete="name"
        placeholder="Your name"
        data-testid={testId}
      />
    </div>
  )
}
