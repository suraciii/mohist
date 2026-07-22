import { Button } from '@/shared/ui/components/button'

export interface ErrorStateProps {
  title?: string
  message?: string
  onRetry?: () => void
  retryLabel?: string
  testId?: string
}

export function ErrorState({
  title = 'Something went wrong',
  message = 'We could not load this page. Please try again.',
  onRetry,
  retryLabel = 'Retry',
  testId = 'error-state',
}: ErrorStateProps) {
  return (
    <div
      className="flex items-center justify-center flex-1 p-6"
      data-testid={testId}
      data-error-state="transient"
    >
      <div className="max-w-md w-full rounded-lg border border-danger-border bg-danger-subtle p-6 text-center">
        <div className="text-base font-semibold text-danger mb-2" data-testid={`${testId}-title`}>
          {title}
        </div>
        <div className="text-sm text-danger-foreground/80 mb-4" data-testid={`${testId}-message`}>
          {message}
        </div>
        {onRetry && (
          <Button
            type="button"
            variant="outline"
            onClick={onRetry}
            data-testid={`${testId}-retry`}
          >
            {retryLabel}
          </Button>
        )}
      </div>
    </div>
  )
}
