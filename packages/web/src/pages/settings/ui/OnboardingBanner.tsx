import { InfoIcon, XIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'

type OnboardingBannerProps = {
  onDismiss: () => void
}

export function OnboardingBanner({ onDismiss }: OnboardingBannerProps) {
  return (
    <div
      className="mb-4 flex items-start gap-3 rounded-xl border border-primary/20 bg-primary/5 px-4 py-3 text-sm text-foreground"
      role="status"
      data-testid="settings-onboarding-banner"
    >
      <InfoIcon className="mt-0.5 size-4 shrink-0 text-primary" aria-hidden="true" />
      <p className="flex-1 text-sm font-medium">
        Start here — select the coder agent model used for workflow tasks
      </p>
      <Button
        type="button"
        variant="ghost"
        size="icon-xs"
        aria-label="Dismiss onboarding banner"
        onClick={onDismiss}
        className="-mr-1 -mt-1"
      >
        <XIcon className="size-3.5" aria-hidden="true" />
      </Button>
    </div>
  )
}
