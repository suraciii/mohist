import { useEffect, useState } from 'react'
import { Loader2Icon, ShieldCheckIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { MaskedCredentialInput } from '@/shared/ui/components/masked-credential-input'

interface CredentialFormStepProps {
  onSubmit: (input: { appToken: string; botToken: string }) => void
  isSubmitting: boolean
  errorMessage: string | null
  submitLabel?: string
}

const APP_TOKEN_PLACEHOLDER = 'xapp-…'
const BOT_TOKEN_PLACEHOLDER = 'xoxb-…'

export function CredentialFormStep({
  onSubmit,
  isSubmitting,
  errorMessage,
  submitLabel = 'Save credentials',
}: CredentialFormStepProps) {
  const [appToken, setAppToken] = useState('')
  const [botToken, setBotToken] = useState('')

  useEffect(() => {
    return () => {
      setAppToken('')
      setBotToken('')
    }
  }, [])

  const canSubmit = appToken.trim().length > 0 && botToken.trim().length > 0 && !isSubmitting

  function handleSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!canSubmit) return
    const payload = { appToken: appToken.trim(), botToken: botToken.trim() }
    setAppToken('')
    setBotToken('')
    onSubmit(payload)
  }

  return (
    <form
      onSubmit={handleSubmit}
      className="space-y-3"
      data-testid="connection-setup-credential-form"
      autoComplete="off"
    >
      <div className="inline-flex items-start gap-1.5 rounded-md border border-info/40 bg-info-subtle px-2 py-1 text-xs text-info">
        <ShieldCheckIcon className="mt-0.5 size-3.5 shrink-0" />
        <span>
          Tokens are sent in the request body and never written to the URL. After a successful save the
          values are cleared from the form.
        </span>
      </div>
      <div className="space-y-2">
        <label className="block text-sm">
          <span className="text-foreground">App token</span>
          <MaskedCredentialInput
            name="appToken"
            placeholder={APP_TOKEN_PLACEHOLDER}
            value={appToken}
            onChange={(event) => setAppToken(event.target.value)}
            aria-label="App token"
            required
            className="mt-1"
            disabled={isSubmitting}
          />
        </label>
        <label className="block text-sm">
          <span className="text-foreground">Bot token</span>
          <MaskedCredentialInput
            name="botToken"
            placeholder={BOT_TOKEN_PLACEHOLDER}
            value={botToken}
            onChange={(event) => setBotToken(event.target.value)}
            aria-label="Bot token"
            required
            className="mt-1"
            disabled={isSubmitting}
          />
        </label>
      </div>
      {errorMessage && (
        <p
          className="text-xs text-danger"
          data-testid="connection-setup-credential-form-error"
          role="alert"
        >
          {errorMessage}
        </p>
      )}
      <Button
        type="submit"
        size="sm"
        disabled={!canSubmit}
        data-testid="connection-setup-credential-form-submit"
      >
        {isSubmitting ? <Loader2Icon className="size-4 animate-spin" /> : null}
        {submitLabel}
      </Button>
    </form>
  )
}
