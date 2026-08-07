import { useState } from 'react'
import { useLogin } from '@/entities/auth'
import { Button } from '@/shared/ui/components/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/shared/ui/components/card'
import { MaskedCredentialInput } from '@/shared/ui/components/masked-credential-input'

export function LoginPage() {
  const login = useLogin()
  const [token, setToken] = useState('')

  const canSubmit = token.trim().length > 0 && !login.isPending

  function handleSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!canSubmit) return
    login.mutate(token.trim())
  }

  return (
    <div className="flex h-svh items-center justify-center bg-background p-4" data-testid="login-page">
      <Card className="w-full max-w-sm">
        <CardHeader>
          <CardTitle>Sign in to Mohist</CardTitle>
          <CardDescription>
            Paste an operator-level token (the admin credential file, or a full-scope personal access
            token) to exchange it for a browser session.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={handleSubmit} className="space-y-3" autoComplete="off">
            <MaskedCredentialInput
              name="token"
              placeholder="moh_admin_…"
              value={token}
              onChange={(event) => setToken(event.target.value)}
              aria-label="Operator token"
              aria-invalid={login.isError}
              required
              className="h-9"
              disabled={login.isPending}
              autoFocus
            />
            {login.isError && (
              <p role="alert" className="text-sm text-destructive">
                {login.error.message || 'Sign in failed'}
              </p>
            )}
            <Button type="submit" className="w-full" disabled={!canSubmit}>
              {login.isPending ? 'Signing in…' : 'Sign in'}
            </Button>
          </form>
        </CardContent>
      </Card>
    </div>
  )
}
