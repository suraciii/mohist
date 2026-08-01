import { useEffect, useRef } from 'react'
import { useParams } from 'react-router-dom'
import { AlertCircleIcon, CheckCircle2Icon, CircleOffIcon, Settings2Icon } from 'lucide-react'
import {
  useAgentConnection,
  useAgentConnectionAccess,
  useClaimAgentConnectionOwner,
  useConfigureAgentConnection,
  useConnectionDiagnostic,
  useManageAgentConnectionAccess,
  useSlackMemberSearchFn,
} from '../../../entities/agent-connection'
import type {
  AgentConnectionClaimOwnerResponse,
  AgentConnectionDetailResponse,
  AccessPolicyState,
  ConnectionDiagnostic,
  SlackMemberSearchEntry,
} from '../../../entities/agent-connection'
import { CardSection } from '@/shared/ui/components/card-section'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'
import { SetupStepList } from './setup-step-list'
import { IdentityPreviewStep } from './identity-preview-step'
import { CredentialFormStep } from './credential-form-step'
import { ClaimOwnerCodeStep } from './claim-owner-code-step'
import { AccessPolicySection } from './access-policy-section'

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

export interface ConnectionDiagnosticPageOperations {
  connectionDetailQuery: {
    data: AgentConnectionDetailResponse | undefined
    isLoading: boolean
  }
  configureMutation: {
    mutate: (input: { appToken: string; botToken: string }) => void
    isPending: boolean
    error: Error | null
    reset: () => void
  }
  claimOwnerMutation: {
    mutate: () => void
    isPending: boolean
    error: Error | null
    data: AgentConnectionClaimOwnerResponse | undefined
    reset: () => void
  }
  accessStateQuery: {
    data: AccessPolicyState | undefined
  }
  manageAccessMutation: {
    mutate: (input: { accessPolicy: 'owner_only' | 'allowlist' | 'anyone'; allowMembers: string[] }) => void
    isPending: boolean
    error: Error | null
    reset: () => void
  }
  searchMembers: (query: string) => Promise<SlackMemberSearchEntry[]>
}

export type ConnectionDiagnosticPageOperationsHook = (
  connectionId: string | undefined,
  setupComplete?: boolean,
) => ConnectionDiagnosticPageOperations

const useDefaultOperations: ConnectionDiagnosticPageOperationsHook = (connectionId, setupComplete) => {
  const detailQuery = useAgentConnection(connectionId)
  const configure = useConfigureAgentConnection(connectionId)
  const claim = useClaimAgentConnectionOwner(connectionId)
  const accessStateQuery = useAgentConnectionAccess(connectionId, setupComplete ?? false)
  const manageAccess = useManageAgentConnectionAccess(connectionId)
  const searchMembers = useSlackMemberSearchFn(connectionId)
  return {
    connectionDetailQuery: {
      data: detailQuery.data,
      isLoading: detailQuery.isLoading,
    },
    configureMutation: {
      mutate: configure.mutate,
      isPending: configure.isPending,
      error: configure.error instanceof Error ? configure.error : null,
      reset: configure.reset,
    },
    claimOwnerMutation: {
      mutate: claim.mutate,
      isPending: claim.isPending,
      error: claim.error instanceof Error ? claim.error : null,
      data: claim.data,
      reset: claim.reset,
    },
    accessStateQuery: { data: accessStateQuery.data },
    manageAccessMutation: {
      mutate: manageAccess.mutate,
      isPending: manageAccess.isPending,
      error: manageAccess.error instanceof Error ? manageAccess.error : null,
      reset: manageAccess.reset,
    },
    searchMembers,
  }
}

export const readOnlyOperations: ConnectionDiagnosticPageOperations = {
  connectionDetailQuery: { data: undefined, isLoading: false },
  configureMutation: {
    mutate: () => undefined,
    isPending: false,
    error: null,
    reset: () => undefined,
  },
  claimOwnerMutation: {
    mutate: () => undefined,
    isPending: false,
    error: null,
    data: undefined,
    reset: () => undefined,
  },
  accessStateQuery: { data: undefined },
  manageAccessMutation: {
    mutate: () => undefined,
    isPending: false,
    error: null,
    reset: () => undefined,
  },
  searchMembers: async () => [],
}

export const useReadOnlyOperations: ConnectionDiagnosticPageOperationsHook = () => readOnlyOperations

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
  operationsHook = useDefaultOperations,
}: {
  dataHook?: ConnectionDiagnosticPageDataHook
  operationsHook?: ConnectionDiagnosticPageOperationsHook
} = {}) {
  const { connectionId } = useParams<{ connectionId: string }>()
  const { data, isLoading, error } = dataHook(connectionId)
  const ops = operationsHook(connectionId, data?.facts.setupProgress === 'complete')
  const { connectionDetailQuery, configureMutation, claimOwnerMutation, accessStateQuery, manageAccessMutation } = ops
  useDocumentTitle(data ? `Connection ${connectionId ?? ''} - Mohist` : 'Connection - Mohist')

  const configureResetRef = useRef<() => void>(() => undefined)
  configureResetRef.current = configureMutation.reset
  const claimResetRef = useRef<() => void>(() => undefined)
  claimResetRef.current = claimOwnerMutation.reset
  const accessResetRef = useRef<() => void>(() => undefined)
  accessResetRef.current = manageAccessMutation.reset

  useEffect(() => {
    return () => {
      claimResetRef.current()
      configureResetRef.current()
      accessResetRef.current()
    }
  }, [])

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
  const setupProgress = facts.setupProgress
  const detail = connectionDetailQuery.data
  const previewBotName = detail?.botName ?? facts.identity.botName
  const previewAppDescription = detail?.appDescription ?? ''
  const previewSlackAppCreationReference = detail?.slackAppCreationReference ?? ''

  const isSetupComplete = setupProgress === 'complete'

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

        {!isSetupComplete && (
          <CardSection title="Setup progress" tone="amber">
            <div className="space-y-2">
              <p className="text-xs text-muted-foreground">
                Setup is owned by the server. Closing, refreshing, or returning on another device resumes at
                the current step.
              </p>
              <SetupStepList setupProgress={setupProgress} />
            </div>
          </CardSection>
        )}

        {setupProgress === 'create_app_credentials' && (
          <CardSection title="Step 1 — Create app & add credentials">
            <div className="space-y-4">
              {connectionDetailQuery.isLoading ? (
                <p className="text-xs text-muted-foreground" data-testid="connection-setup-identity-loading">
                  Loading identity preview...
                </p>
              ) : (
                <IdentityPreviewStep
                  botName={previewBotName}
                  appDescription={previewAppDescription}
                  slackAppCreationReference={previewSlackAppCreationReference}
                />
              )}
              <div className="border-t border-border pt-4">
                <p className="mb-2 text-sm font-medium text-foreground">Add credentials</p>
                <CredentialFormStep
                  onSubmit={(input) => {
                    configureMutation.reset()
                    configureMutation.mutate(input)
                  }}
                  isSubmitting={configureMutation.isPending}
                  errorMessage={configureMutation.error?.message ?? null}
                />
              </div>
            </div>
          </CardSection>
        )}

        {setupProgress === 'waiting_for_slack_service' && (
          <CardSection title="Step 2 — Waiting for Slack service" tone="amber">
            <p
              className="text-sm text-muted-foreground"
              data-testid="connection-setup-waiting-for-service"
            >
              Credentials are saved. Mohist is waiting for the Slack service (mohist-slack) to come online and
              verify the tokens. Progress is preserved; no action is needed here.
            </p>
          </CardSection>
        )}

        {setupProgress === 'fix_slack_setup' && (
          <CardSection title="Step 3 — Fix Slack setup" tone="amber">
            <p
              className="text-sm text-muted-foreground"
              data-testid="connection-setup-fix-step"
            >
              The Slack service reported a problem with this Connection. Re-check the credentials and the
              workspace install, then wait for the service to re-verify.
            </p>
          </CardSection>
        )}

        {setupProgress === 'claim_owner' && (
          <CardSection title="Step 4 — Claim owner">
            <ClaimOwnerCodeStep
              code={claimOwnerMutation.data?.code ?? null}
              expiresAt={claimOwnerMutation.data?.expiresAt ?? null}
              onGenerate={() => {
                claimOwnerMutation.reset()
                claimOwnerMutation.mutate()
              }}
              isGenerating={claimOwnerMutation.isPending}
              errorMessage={claimOwnerMutation.error?.message ?? null}
            />
          </CardSection>
        )}

        {isSetupComplete && accessStateQuery.data && (
          <AccessPolicySection
            accessPolicy={accessStateQuery.data.accessPolicy}
            allowMembers={accessStateQuery.data.allowMembers}
            ownerSlackUserId={detail?.connection.ownerSlackUserId ?? null}
            anyoneDisclosure={accessStateQuery.data.anyoneDisclosure}
            onSubmit={(input) => {
              manageAccessMutation.reset()
              manageAccessMutation.mutate(input)
            }}
            isSubmitting={manageAccessMutation.isPending}
            errorMessage={manageAccessMutation.error?.message ?? null}
            searchMembers={ops.searchMembers}
          />
        )}

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
