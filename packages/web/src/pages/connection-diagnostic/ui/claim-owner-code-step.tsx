import { Loader2Icon, RefreshCwIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'

interface ClaimOwnerCodeStepProps {
  code: string | null
  expiresAt: string | null
  onGenerate: () => void
  isGenerating: boolean
  errorMessage: string | null
}

function formatExpiry(expiresAt: string | null): string {
  if (!expiresAt) return ''
  const expires = new Date(expiresAt)
  if (Number.isNaN(expires.getTime())) return ''
  return expires.toLocaleString()
}

export function ClaimOwnerCodeStep({
  code,
  expiresAt,
  onGenerate,
  isGenerating,
  errorMessage,
}: ClaimOwnerCodeStepProps) {
  const hasCode = Boolean(code)

  return (
    <div className="space-y-3" data-testid="connection-setup-claim-owner">
      <p className="text-sm text-muted-foreground">
        Send this one-time code in a Slack direct message to the Bot. The code proves the App can receive
        direct messages and binds ownership to the sender.
      </p>
      {hasCode ? (
        <div
          className="rounded-md border border-amber-300 bg-amber-50 p-3 text-sm"
          data-testid="connection-setup-claim-owner-code-box"
        >
          <div className="text-xs uppercase tracking-wide text-amber-700">One-time claim code</div>
          <div
            className="mt-1 break-all font-mono text-lg text-foreground"
            data-testid="connection-setup-claim-owner-code"
          >
            {code}
          </div>
          <div
            className="mt-1 text-xs text-muted-foreground"
            data-testid="connection-setup-claim-owner-expires-at"
          >
            Expires {formatExpiry(expiresAt)}
          </div>
          <p className="mt-2 text-xs text-muted-foreground">
            The code is shown once. Leaving this page discards the displayed code; regenerating
            invalidates the previous one server-side.
          </p>
        </div>
      ) : (
        <div
          className="rounded-md border border-border bg-background/60 p-3 text-sm text-muted-foreground"
          data-testid="connection-setup-claim-owner-empty"
        >
          No active claim code. Generate one to bind an owner.
        </div>
      )}
      {errorMessage && (
        <p
          className="text-xs text-danger"
          data-testid="connection-setup-claim-owner-error"
          role="alert"
        >
          {errorMessage}
        </p>
      )}
      <Button
        size="sm"
        variant="outline"
        onClick={onGenerate}
        disabled={isGenerating}
        data-testid="connection-setup-claim-owner-generate"
      >
        {isGenerating ? <Loader2Icon className="size-4 animate-spin" /> : <RefreshCwIcon />}
        {hasCode ? 'Regenerate code' : 'Generate code'}
      </Button>
    </div>
  )
}
