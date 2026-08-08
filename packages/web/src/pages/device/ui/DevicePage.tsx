import { useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { ApiError } from '@/shared/api/client'
import { useDeviceDecision, useVerifyDeviceCode } from '@/entities/auth'
import { Button } from '@/shared/ui/components/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/shared/ui/components/card'
import { Input } from '@/shared/ui/components/input'

/**
 * RFC 8628 confirmation page (docs/auth.md "远程 CLI：设备授权登录"):
 * the CLI shows this link; the user types the code, reviews what is
 * being authorized and approves or denies. Only reachable behind a
 * logged-in Web session (AuthGate); the server additionally requires
 * the session on verify/decision.
 */
export function DevicePage() {
  const [searchParams] = useSearchParams()
  const [userCode, setUserCode] = useState(() => normalize(searchParams.get('user_code') ?? ''))
  const [flowId, setFlowId] = useState<string | null>(null)
  const [clientName, setClientName] = useState<string | null>(null)
  const [decision, setDecision] = useState<'approved' | 'denied' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const verify = useVerifyDeviceCode()
  const decide = useDeviceDecision()

  const canVerify = userCode.length === 8 && !verify.isPending

  function handleVerify(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!canVerify) return
    setError(null)
    verify.mutate(userCode, {
      onSuccess: (result) => {
        setFlowId(result.flowId)
        setClientName(result.clientName)
      },
      onError: (failure) => setError(messageOf(failure)),
    })
  }

  function handleDecision(next: 'approved' | 'denied') {
    if (!flowId || decide.isPending) return
    setError(null)
    decide.mutate({ flowId, decision: next }, {
      onSuccess: () => setDecision(next),
      onError: (failure) => setError(messageOf(failure)),
    })
  }

  return (
    <div className="flex min-h-svh items-center justify-center bg-background p-4" data-testid="device-page">
      <Card className="w-full max-w-md">
        <CardHeader>
          <CardTitle>Authorize a device</CardTitle>
          <CardDescription>
            A command-line client is waiting for confirmation. Enter the code it printed to review and
            approve the sign-in.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          {decision === null && (
            <>
              <form onSubmit={handleVerify} className="space-y-3" autoComplete="off">
                <Input
                  name="userCode"
                  placeholder="XXXX-XXXX"
                  aria-label="Confirmation code"
                  value={userCode}
                  onChange={(event) => setUserCode(normalize(event.target.value))}
                  className="font-mono text-lg tracking-widest uppercase"
                  maxLength={8}
                  autoFocus
                />
                {flowId === null && (
                  <Button type="submit" className="w-full" disabled={!canVerify}>
                    {verify.isPending ? 'Checking…' : 'Continue'}
                  </Button>
                )}
              </form>
              {flowId !== null && (
                <div className="space-y-4" data-testid="device-confirm">
                  <p className="text-sm text-muted-foreground">
                    {clientName ? (
                      <>
                        <span className="font-medium text-foreground">{clientName}</span> is requesting
                        access to <span className="font-medium text-foreground">all Mohist operations</span>.
                      </>
                    ) : (
                      <>A remote client is requesting access to all Mohist operations.</>
                    )}
                  </p>
                  <div className="flex gap-2">
                    <Button variant="outline" className="flex-1" disabled={decide.isPending} onClick={() => handleDecision('denied')}>
                      Deny
                    </Button>
                    <Button className="flex-1" disabled={decide.isPending} onClick={() => handleDecision('approved')}>
                      {decide.isPending ? 'Confirming…' : 'Approve'}
                    </Button>
                  </div>
                </div>
              )}
            </>
          )}
          {decision === 'approved' && (
            <p role="status" className="text-sm text-emerald-600" data-testid="device-approved">
              Approved. You can close this page — the command line client is now signed in.
            </p>
          )}
          {decision === 'denied' && (
            <p role="status" className="text-sm text-destructive" data-testid="device-denied">
              Denied. The command line client will not be signed in.
            </p>
          )}
          {error && (
            <p role="alert" className="text-sm text-destructive">
              {error}
            </p>
          )}
        </CardContent>
      </Card>
    </div>
  )
}

function normalize(input: string): string {
  // Same confusion-free alphabet as the server (no I/O/0/1); hyphens and
  // spaces are ignored so the XXXX-XXXX grouped form works.
  return input
    .toUpperCase()
    .replace(/[^ABCDEFGHJKLMNPQRSTUVWXYZ23456789]/g, '')
    .slice(0, 8)
}

function messageOf(failure: unknown): string {
  if (failure instanceof ApiError) {
    if (failure.status === 404) return 'Code not found. Check the code and try again.'
    if (failure.status === 410) return 'This code has expired. Run mo auth login again for a new code.'
    if (failure.status === 429) return 'Too many attempts. Wait a minute and try again.'
    return failure.message || 'Something went wrong. Try again.'
  }
  return 'Something went wrong. Try again.'
}
