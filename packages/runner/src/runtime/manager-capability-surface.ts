// Manager request-capability surface mirrored from
// `Mohist.Workflow.Definition/ManagerCapabilityCatalog.cs`. The runner
// broker and the CLI manager-mode admission use the same vocabulary, so a
// request the CLI would reject in Manager mode can never become a
// credential-bearing child here either. Keep this mirror in lockstep with
// the C# catalog.

export const MANAGER_REPLY_CAPABILITY = 'manager.reply'

const MANAGEMENT_CAPABILITIES = new Set([
  'workspace.status',
  'agent.list',
  'agent.view',
  'agent.create-or-mount',
  'connection.list',
  'connection.view',
  'connection.diagnostics',
  'connection.access-policy',
  'connection.enable',
  'connection.disable',
  'owner.claim',
  'owner.transfer',
])

export type ManagerRequestKind = 'management' | 'reply'

export interface ManagerRequestLimits {
  readonly reply: number
  readonly management: number
}

export const DEFAULT_MANAGER_REQUEST_LIMITS: ManagerRequestLimits = {
  reply: 5,
  management: 64,
}

export function resolveManagerRequestCapability(args: readonly string[]): string | null {
  if (args.length === 0) return null
  if (args[0] === 'slack') {
    const verb = args[1]
    if (verb === 'message') return args[2] === 'send' ? MANAGER_REPLY_CAPABILITY : null
    switch (verb) {
      case 'status':
        return 'workspace.status'
      case 'list':
        return hasOption(args, '--workspace-team') ? 'agent.list' : 'connection.list'
      case 'view':
      case 'diagnostics':
        return 'connection.diagnostics'
      case 'create':
        return 'agent.create-or-mount'
      case 'enable':
        return 'connection.enable'
      case 'disable':
        return 'connection.disable'
      case 'claim-owner':
        return 'owner.claim'
      case 'transfer-owner':
        return 'owner.transfer'
      case 'edit':
        return hasOption(args, '--access-policy') || hasOption(args, '--allow-member')
          ? !hasOption(args, '--bot-name') && !hasOption(args, '--avatar-hash')
            ? 'connection.access-policy'
            : null
          : null
      default:
        return null
    }
  }
  if (args[0] === 'agent') {
    switch (args[1]) {
      case 'list':
        return 'agent.list'
      case 'view':
        return 'agent.view'
      case 'create':
        return 'agent.create-or-mount'
      default:
        return null
    }
  }
  return null
}

export function managerRequestKind(capability: string | null): ManagerRequestKind | null {
  if (capability === MANAGER_REPLY_CAPABILITY) return 'reply'
  if (capability !== null && MANAGEMENT_CAPABILITIES.has(capability)) return 'management'
  return null
}

// Mirrors `ManagerCliMode.IsHelpRequest` plus the bare-invocation and
// mode-flag handling that precede the CLI's allowlist rejection, so the
// broker admits exactly the invocations Manager-mode `mo` itself accepts.
export function isManagerUsageRequest(args: readonly string[]): boolean {
  if (args.length === 0) return true
  const effective = args.filter((arg) => arg !== '--manager' && arg !== '--manager=true')
  if (effective.length === 0) return true
  return effective.some(
    (arg) => arg === '--help' || arg === '-h' || arg === '-?' || arg === '/?' || arg.startsWith('--help='),
  )
}

function hasOption(args: readonly string[], option: string): boolean {
  for (const arg of args) {
    if (arg === option || arg.startsWith(`${option}=`)) return true
  }
  return false
}
