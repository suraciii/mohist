import { ExternalLinkIcon, Loader2Icon, TerminalIcon } from 'lucide-react'
import type { ManagedSlackAppProjection } from '../../../entities/agent-connection'

/**
 * The Server projects one action the caller can execute. Everything else it
 * reports — App create, manifest application, unknown-outcome reconciliation,
 * Socket hello — is its own work, so the Web shows it as a waiting state
 * instead of inventing a human step.
 */
export type SetupPrimaryActionKind = 'approve_install' | 'host_command' | 'waiting' | 'ready'

const CREDENTIAL_STEP_ACTIONS = new Set(['provide_credentials', 'configure_socket_credentials'])

export function resolveSetupPrimaryAction(
  app: Pick<ManagedSlackAppProjection, 'nextAction' | 'installUrl'>,
): SetupPrimaryActionKind {
  if (app.nextAction === 'approve_install' && app.installUrl) return 'approve_install'
  if (CREDENTIAL_STEP_ACTIONS.has(app.nextAction)) return 'host_command'
  if (app.nextAction === 'ready') return 'ready'
  return 'waiting'
}

interface SetupPrimaryActionProps {
  app: Pick<ManagedSlackAppProjection, 'nextAction' | 'installUrl'>
  agentId: string
}

export function SetupPrimaryAction({ app, agentId }: SetupPrimaryActionProps) {
  const kind = resolveSetupPrimaryAction(app)

  if (kind === 'ready') {
    return (
      <p className="text-sm text-muted-foreground" data-testid="connection-setup-primary-action" data-action="ready">
        The managed App is ready. Nothing is needed from Slack here.
      </p>
    )
  }

  if (kind === 'approve_install') {
    return (
      <a
        href={app.installUrl ?? ''}
        target="_blank"
        rel="noreferrer noopener"
        className="inline-flex items-center gap-2 rounded-md bg-primary px-3 py-2 text-sm font-medium text-primary-foreground hover:bg-primary/90"
        data-testid="connection-setup-primary-action"
        data-action="approve_install"
      >
        Approve in Slack
        <ExternalLinkIcon className="size-4" />
      </a>
    )
  }

  if (kind === 'host_command') {
    return (
      <div
        className="rounded-md border border-border bg-muted/40 p-3 text-sm"
        data-testid="connection-setup-primary-action"
        data-action="host_command"
      >
        <div className="flex items-start gap-2">
          <TerminalIcon className="mt-0.5 size-4 shrink-0 text-muted-foreground" />
          <div className="space-y-2">
            <p className="text-foreground">
              Run this command on the Mohist host and enter the two tokens at its hidden prompt. Automation names a
              protected file with <code>--credentials-file</code>.
            </p>
            <code
              className="block overflow-x-auto rounded bg-background px-2 py-1.5 text-xs text-foreground"
              data-testid="connection-setup-host-command"
            >
              mo slack install-agent {agentId}
            </code>
          </div>
        </div>
      </div>
    )
  }

  return (
    <p
      className="flex items-start gap-2 text-sm text-muted-foreground"
      data-testid="connection-setup-primary-action"
      data-action="waiting"
    >
      <Loader2Icon className="mt-0.5 size-4 shrink-0 animate-spin" />
      Mohist is completing this step. Nothing is needed here; this page updates when it finishes.
    </p>
  )
}
