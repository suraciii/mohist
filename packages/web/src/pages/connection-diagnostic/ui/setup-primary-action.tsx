import { ExternalLinkIcon, Loader2Icon, TerminalIcon } from 'lucide-react'
import type { ManagedSlackAppProjection } from '../../../entities/agent-connection'

/**
 * The Server projects one action the caller can execute. Everything else it
 * reports — App create, manifest application, unknown-outcome reconciliation,
 * Socket hello — is its own work, so the Web shows it as a waiting state
 * instead of inventing a human step.
 */
export type SetupPrimaryActionKind =
  | 'approve_install'
  | 'host_command'
  | 'claim_owner'
  | 'repair_agent'
  | 'waiting'
  | 'ready'

const CREDENTIAL_STEP_ACTIONS = new Set(['provide_credentials', 'configure_socket_credentials'])

export function resolveSetupPrimaryAction(
  app: Pick<ManagedSlackAppProjection, 'nextAction' | 'installUrl'>,
): SetupPrimaryActionKind {
  if (app.nextAction === 'approve_install' && app.installUrl) return 'approve_install'
  if (CREDENTIAL_STEP_ACTIONS.has(app.nextAction)) return 'host_command'
  if (app.nextAction === 'claim_owner') return 'claim_owner'
  if (app.nextAction === 'repair_agent') return 'repair_agent'
  if (app.nextAction === 'ready') return 'ready'
  return 'waiting'
}

/**
 * The target every copied host command carries. The Agent id resumes the
 * managed installation, the Connection id owns the Owner claim, and the Project
 * reference and Workspace selector keep the copied command on the target the
 * page shows; none of them is a credential, and the claim code is not one of
 * them because only the host command's own response ever carries a code.
 */
export interface SetupHostCommandTarget {
  agentId: string
  connectionId: string
  projectRef: string | null
  workspaceTeamId?: string | null
}

/** The exact Bot DM destination an Owner claim code is sent to. */
export function botDmDestination(botName: string | null | undefined): string | null {
  if (!botName) return null
  return `Direct message with the ${botName} Bot`
}

interface SetupPrimaryActionProps {
  app: Pick<ManagedSlackAppProjection, 'nextAction' | 'installUrl'>
  target: SetupHostCommandTarget
  /** Verified Bot name, so the claim names the identity the user writes to. */
  botName?: string | null
  /** Existing Agent repair surface, already project-scoped by the caller. */
  agentRepair?: { label: string; href: string } | null
}

export function SetupPrimaryAction({ app, target, botName, agentRepair }: SetupPrimaryActionProps) {
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

  if (kind === 'claim_owner') {
    const destination = botDmDestination(botName)
    return (
      <div
        className="rounded-md border border-border bg-muted/40 p-3 text-sm"
        data-testid="connection-setup-primary-action"
        data-action="claim_owner"
      >
        <div className="flex items-start gap-2">
          <TerminalIcon className="mt-0.5 size-4 shrink-0 text-muted-foreground" />
          <div className="space-y-2">
            <p className="text-foreground">
              Owner claim is the one remaining step. Run this command on the Mohist host; its response issues the
              one-time code and is the only place the code appears. This page never shows or issues one, and refreshing
              it leaves an outstanding code valid.
            </p>
            <code
              className="block overflow-x-auto rounded bg-background px-2 py-1.5 text-xs text-foreground"
              data-testid="connection-setup-host-command"
            >
              mo slack claim-owner {target.connectionId}
              {target.projectRef ? ` --project ${target.projectRef}` : ''}
            </code>
            <p className="text-muted-foreground" data-testid="connection-setup-claim-destination">
              {destination
                ? `Send the code in a ${destination.toLowerCase()}. Only a current full Workspace member can claim.`
                : 'Send the code in a direct message with this Connection’s Slack Bot. Only a current full Workspace member can claim.'}
            </p>
          </div>
        </div>
      </div>
    )
  }

  if (kind === 'repair_agent') {
    return (
      <div className="space-y-2" data-testid="connection-setup-primary-action" data-action="repair_agent">
        <p className="text-sm text-foreground">
          Slack setup is complete, but the Agent cannot accept work yet. Open the existing Agent repair surface to
          finish its Runtime and Runner configuration.
        </p>
        {agentRepair && (
          <a
            href={agentRepair.href}
            className="inline-flex items-center gap-2 rounded-md bg-primary px-3 py-2 text-sm font-medium text-primary-foreground hover:bg-primary/90"
            data-testid="connection-setup-agent-repair-link"
          >
            {agentRepair.label}
            <ExternalLinkIcon className="size-4" />
          </a>
        )}
      </div>
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
              mo slack install-agent {target.agentId}
              {target.projectRef ? ` --project ${target.projectRef}` : ''}
              {target.workspaceTeamId ? ` --workspace-team ${target.workspaceTeamId}` : ''}
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
