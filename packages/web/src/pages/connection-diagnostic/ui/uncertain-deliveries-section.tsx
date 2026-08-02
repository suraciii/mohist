import { useEffect, useMemo, useRef, useState } from 'react'
import { AlertTriangleIcon, Loader2Icon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { CardSection } from '@/shared/ui/components/card-section'
import type { SlackOutboxEntry } from '../../../entities/agent-connection'

export interface UncertainDeliveriesSectionProps {
  entries: readonly SlackOutboxEntry[]
  isLoading: boolean
  errorMessage: string | null
  isResending: boolean
  onResend: (deliveryId: string) => void
}

function formatTimestamp(value: string | null) {
  if (!value) return 'unknown'
  const parsed = new Date(value)
  if (Number.isNaN(parsed.valueOf())) return value
  return parsed.toLocaleString()
}

function decodePayloadText(payloadJson: string): string | null {
  try {
    const parsed: unknown = JSON.parse(payloadJson)
    if (parsed && typeof parsed === 'object' && 'text' in parsed && typeof (parsed as { text: unknown }).text === 'string') {
      return (parsed as { text: string }).text
    }
  } catch {
    return null
  }
  return null
}

function truncate(value: string, max: number) {
  if (value.length <= max) return value
  return `${value.slice(0, max - 1)}…`
}

export function UncertainDeliveriesSection({
  entries,
  isLoading,
  errorMessage,
  isResending,
  onResend,
}: UncertainDeliveriesSectionProps) {
  const uncertainEntries = useMemo(
    () => entries.filter((entry) => entry.state === 'delivery_uncertain'),
    [entries],
  )

  if (isLoading) {
    return (
      <CardSection title="Delivery uncertain" tone="amber">
        <p className="flex items-center gap-2 text-sm text-muted-foreground" data-testid="uncertain-deliveries-loading">
          <Loader2Icon className="size-4 animate-spin" aria-hidden />
          Loading delivery state…
        </p>
      </CardSection>
    )
  }

  if (errorMessage) {
    return (
      <CardSection title="Delivery uncertain" tone="red">
        <p className="text-sm text-danger" data-testid="uncertain-deliveries-error">
          {errorMessage}
        </p>
      </CardSection>
    )
  }

  if (uncertainEntries.length === 0) {
    return (
      <CardSection title="Delivery uncertain" tone="green">
        <p className="text-sm text-muted-foreground" data-testid="uncertain-deliveries-empty">
          No outbound deliveries are stuck in Delivery uncertain.
        </p>
      </CardSection>
    )
  }

  return (
    <CardSection title="Delivery uncertain" tone="amber">
      <p className="mb-3 text-sm text-muted-foreground" data-testid="uncertain-deliveries-intro">
        These outbound replies never received a confirmed Slack outcome. Resending is safe for the
        Connection but may produce a duplicate reply if Slack actually accepted the original post.
      </p>
      <ul className="space-y-3" data-testid="uncertain-deliveries-list">
        {uncertainEntries.map((entry) => (
          <UncertainDeliveryRow
            key={entry.id}
            entry={entry}
            isResending={isResending}
            onResend={onResend}
          />
        ))}
      </ul>
    </CardSection>
  )
}

interface UncertainDeliveryRowProps {
  entry: SlackOutboxEntry
  isResending: boolean
  onResend: (deliveryId: string) => void
}

function UncertainDeliveryRow({ entry, isResending, onResend }: UncertainDeliveryRowProps) {
  const [confirming, setConfirming] = useState(false)
  const payloadText = useMemo(() => decodePayloadText(entry.payloadJson), [entry.payloadJson])
  const text = payloadText ?? truncate(entry.payloadJson, 140)
  const cancelRef = useRef<() => void>(() => undefined)
  cancelRef.current = () => setConfirming(false)

  useEffect(() => () => cancelRef.current(), [])

  return (
    <li className="rounded border border-border bg-card/30 p-3 text-sm" data-testid="uncertain-delivery-row">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="font-mono text-xs text-muted-foreground">{entry.id}</span>
        <span className="text-xs text-muted-foreground">
          uncertain since {formatTimestamp(entry.deliveryUncertainAt)}
        </span>
      </div>
      <p className="mt-2 text-foreground">{text}</p>
      {entry.lastError && (
        <p className="mt-2 text-xs text-warning" data-testid="uncertain-delivery-reason">
          Reason: {entry.lastError}
        </p>
      )}
      <div className="mt-3 flex flex-wrap items-center gap-2">
        {!confirming && (
          <Button
            type="button"
            variant="outline"
            size="sm"
            disabled={isResending}
            onClick={() => setConfirming(true)}
            data-testid="uncertain-delivery-resend-button"
          >
            Resend
          </Button>
        )}
        {confirming && (
          <div
            className="flex w-full items-start gap-2 rounded border border-warning-border bg-warning-subtle p-3 text-xs text-warning"
            data-testid="uncertain-delivery-resend-warning"
            role="alert"
          >
            <AlertTriangleIcon className="mt-0.5 size-4 shrink-0" aria-hidden />
            <div className="flex-1 space-y-1">
              <p>
                Slack may have already delivered this reply silently. Resending can produce a duplicate
                Slack message even though the underlying AgentJob/AgentTurn result is unchanged.
              </p>
              <p className="text-warning/90">
                Inspect the authoritative execution result before committing.
              </p>
              <div className="flex flex-wrap gap-2 pt-1">
                <Button
                  type="button"
                  size="sm"
                  variant="destructive"
                  disabled={isResending}
                  onClick={() => {
                    setConfirming(false)
                    onResend(entry.id)
                  }}
                  data-testid="uncertain-delivery-resend-confirm"
                >
                  {isResending ? 'Resending…' : 'Resend anyway'}
                </Button>
                <Button
                  type="button"
                  size="sm"
                  variant="ghost"
                  disabled={isResending}
                  onClick={() => setConfirming(false)}
                  data-testid="uncertain-delivery-resend-cancel"
                >
                  Cancel
                </Button>
              </div>
            </div>
          </div>
        )}
      </div>
    </li>
  )
}