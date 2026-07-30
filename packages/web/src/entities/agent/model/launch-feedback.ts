import type { AgentReadinessResult } from '../api/client'

export type AgentLaunchFeedbackKind =
  | 'back-pressure'
  | 'runner-offline'
  | 'needs-setup'
  | 'execution-unavailable'

export interface AgentLaunchFeedback {
  kind: AgentLaunchFeedbackKind
  title: string
  message: string
  nextAction: string
}

export function getAgentAvailabilityFeedback(waitingReason: string | null | undefined): AgentLaunchFeedback {
  switch (waitingReason) {
    case 'no-online-runner':
      return {
        kind: 'runner-offline',
        title: 'Runner offline',
        message: 'No available runner is online for this Agent.',
        nextAction: 'Connect a runner, then retry the launch.',
      }
    case 'capacity-full':
      return {
        kind: 'back-pressure',
        title: 'Launch waiting for capacity',
        message: 'Runner capacity is full; this is an Availability wait, not a configuration gap.',
        nextAction: 'Wait for a runner slot to free up, then retry the launch.',
      }
    case 'concurrency-limit':
      return {
        kind: 'back-pressure',
        title: 'Launch waiting for capacity',
        message: 'This Agent is at its concurrency limit; active work must finish before another run starts.',
        nextAction: 'Wait for an active run to finish, then retry the launch.',
      }
    case 'dispatch-pending':
    default:
      return {
        kind: 'back-pressure',
        title: 'Launch waiting for dispatch',
        message: 'The launch is waiting for dispatch and is not a configuration gap.',
        nextAction: 'Wait for dispatch to complete, then retry with the same launch intent if needed.',
      }
  }
}

type LaunchErrorLike = {
  code?: unknown
  message?: unknown
}

export function getAgentLaunchErrorFeedback(
  error: unknown,
  readiness?: AgentReadinessResult | null,
): AgentLaunchFeedback | null {
  const candidate = error as LaunchErrorLike | null
  const code = typeof candidate?.code === 'string' ? candidate.code.toLowerCase() : ''
  const message = typeof candidate?.message === 'string' ? candidate.message.toLowerCase() : ''

  if (code === 'agent_needs_setup' || readiness?.conclusion === 'Needs setup') {
    return {
      kind: 'needs-setup',
      title: 'Configuration needs setup',
      message: 'The server has not accepted this Agent\'s execution definition.',
      nextAction: 'Fix the listed gaps in Agent settings, then retry the launch.',
    }
  }

  if (
    code === 'no_available_runner'
    || code === 'no-online-runner'
    || message.includes('no available runner')
    || message.includes('no runner is online')
  ) {
    return getAgentAvailabilityFeedback('no-online-runner')
  }

  if (
    code === 'capacity-full'
    || code === 'concurrency-limit'
    || code === 'dispatch-pending'
    || message.includes('capacity full')
    || message.includes('concurrency limit')
    || message.includes('dispatch pending')
  ) {
    return getAgentAvailabilityFeedback(
      code === 'concurrency-limit'
        ? 'concurrency-limit'
        : code === 'dispatch-pending'
          ? 'dispatch-pending'
          : 'capacity-full',
    )
  }

  if (
    code === 'external_agent_unavailable'
    || code === 'runtime-unavailable'
    || code === 'execution-unavailable'
    || message.includes('external agent unavailable')
    || message.includes('external agent is unavailable')
    || message.includes('runtime unavailable')
    || message.includes('execution unavailable')
    || (message.includes('backend') && message.includes('unavailable'))
  ) {
    return {
      kind: 'execution-unavailable',
      title: 'Execution backend unavailable',
      message: 'The configured execution backend cannot run right now; the external agent is unavailable.',
      nextAction: 'Wait for the runtime or provider to recover, then retry the launch.',
    }
  }

  return null
}
