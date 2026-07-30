import { useParams } from 'react-router-dom'
import { AlertCircleIcon, CheckCircle2Icon, CircleOffIcon, Settings2Icon } from 'lucide-react'
import { useConnectionDiagnostic } from '../../../entities/agent-connection'
import type { ConnectionDiagnostic } from '../../../entities/agent-connection'
import { CardSection } from '@/shared/ui/components/card-section'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'

export interface ConnectionDiagnosticPageData {
  data: ConnectionDiagnostic | undefined
  isLoading: boolean
  error: Error | null
}

export type ConnectionDiagnosticPageDataHook = (connectionId: string | undefined) => ConnectionDiagnosticPageData

const useDefaultData: ConnectionDiagnosticPageDataHook = (connectionId) => {
  const { data, isLoading, error } = useConnectionDiagnostic(connectionId)
  return { data, isLoading, error: error instanceof Error ? error : null }
}

function label(value: string | null | undefined) {
  if (!value) return 'Unknown'
  return value.replaceAll('_', ' ')
}

function display(value: string | boolean | null | undefined) {
  if (typeof value === 'boolean') return value ? 'Online' : 'Offline'
  return value ?? 'Unknown'
}

function SummaryIcon({ state }: { state: string }) {
  if (state === 'healthy') return <CheckCircle2Icon className="size-5 text-success" />
  if (state === 'disabled') return <CircleOffIcon className="size-5 text-muted-foreground" />
  if (state === 'agent_needs_setup' || state === 'setup_incomplete') return <Settings2Icon className="size-5 text-warning" />
  return <AlertCircleIcon className="size-5 text-danger" />
}

function FactRow({ name, value }: { name: string; value: string | boolean | null | undefined }) {
  return (
    <div className="grid grid-cols-[minmax(8rem,1fr)_minmax(0,2fr)] gap-4 py-2 text-sm">
      <dt className="text-muted-foreground">{name}</dt>
      <dd className="min-w-0 break-words text-foreground">{display(value)}</dd>
    </div>
  )
}

export function ConnectionDiagnosticPage({
  dataHook = useDefaultData,
}: {
  dataHook?: ConnectionDiagnosticPageDataHook
} = {}) {
  const { connectionId } = useParams<{ connectionId: string }>()
  const { data, isLoading, error } = dataHook(connectionId)
  useDocumentTitle(data ? `Connection ${connectionId ?? ''} - Mohist` : 'Connection - Mohist')

  if (isLoading) {
    return <div className="flex flex-1 items-center justify-center text-sm text-muted-foreground">Loading connection...</div>
  }

  if (error || !data) {
    return (
      <div className="flex flex-1 items-center justify-center text-sm text-danger" data-testid="connection-diagnostic-error">
        {error?.message ?? 'Connection was not found.'}
      </div>
    )
  }

  const { facts } = data
  return (
    <main className="flex-1 min-w-0 overflow-y-auto" data-testid="connection-diagnostic-page">
      <div className="mx-auto max-w-3xl space-y-5 px-4 py-6 sm:px-6">
        <header>
          <p className="text-sm text-muted-foreground">Slack Connection</p>
          <h1 className="mt-1 break-all text-2xl font-semibold text-foreground">{connectionId}</h1>
        </header>

        <CardSection title="Current status" icon={<SummaryIcon state={data.primaryState} />} tone={data.primaryState === 'healthy' ? 'green' : 'default'}>
          <div className="space-y-3">
            <div className="text-lg font-medium capitalize text-foreground" data-testid="connection-diagnostic-primary-state">{label(data.primaryState)}</div>
            <p className="text-sm text-muted-foreground" data-testid="connection-diagnostic-reason">{data.reason}</p>
            <div className="border-l-2 border-info pl-3 text-sm font-medium text-foreground" data-testid="connection-diagnostic-next-action">
              {data.nextAction}
            </div>
          </div>
        </CardSection>

        <details className="border-y border-border py-3" data-testid="connection-diagnostic-facts">
          <summary className="cursor-pointer text-sm font-medium text-foreground">Supporting facts</summary>
          <dl className="mt-3 divide-y divide-border">
            <FactRow name="Setup" value={label(facts.setupProgress)} />
            <FactRow name="Desired state" value={label(facts.desiredState)} />
            <FactRow name="Connection health" value={label(facts.connectionHealth)} />
            <FactRow name="Credential status" value={label(facts.credentialStatus)} />
            <FactRow name="Service" value={facts.adapterOnline} />
            <FactRow name="Owner" value={label(facts.ownerAvailability)} />
            <FactRow name="Agent readiness" value={label(facts.agentReadiness)} />
            <FactRow name="Verification" value={label(facts.identity.verificationStatus)} />
            <FactRow name="Slack Bot name" value={facts.identity.verifiedBotName} />
            <FactRow name="Connection Bot name" value={facts.identity.botName} />
            <FactRow name="Agent name" value={facts.identity.agentName} />
            <FactRow name="Slack avatar" value={facts.identity.verifiedBotIconUrl} />
            <FactRow name="Connection avatar" value={facts.identity.avatarHash} />
            <FactRow name="Identity drift" value={facts.identity.driftKinds.map(label).join(', ') || 'None'} />
            {facts.healthReason && <FactRow name="Health reason" value={facts.healthReason} />}
          </dl>
        </details>
      </div>
    </main>
  )
}
