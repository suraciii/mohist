import '@testing-library/jest-dom'
import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { SetupPrimaryAction, botDmDestination, resolveSetupPrimaryAction } from './setup-primary-action'
import type { ManagedSlackAppProjection } from '../../../entities/agent-connection'

function makeApp(overrides: Partial<ManagedSlackAppProjection> = {}): ManagedSlackAppProjection {
  return {
    appLifecycle: 'created',
    authorization: 'authorized',
    manifestState: 'applied',
    transportKind: 'socket',
    transportReadiness: 'ready',
    nextAction: 'wait_for_operation',
    bindingState: 'bound',
    installUrl: null,
    unknownOutcome: null,
    errorClass: null,
    deletedAt: null,
    ...overrides,
  }
}

const target = { agentId: 'agent-1', connectionId: 'conn-1', projectRef: 'Test' }

describe('resolveSetupPrimaryAction', () => {
  it('offers the install link only when Slack returned one', () => {
    expect(
      resolveSetupPrimaryAction(makeApp({ nextAction: 'approve_install', installUrl: 'https://slack.example/oauth' })),
    ).toBe('approve_install')
    expect(resolveSetupPrimaryAction(makeApp({ nextAction: 'approve_install', installUrl: null }))).toBe('waiting')
  })

  it('offers the protected host command at a credential step', () => {
    for (const nextAction of ['provide_credentials', 'configure_socket_credentials']) {
      expect(resolveSetupPrimaryAction(makeApp({ nextAction }))).toBe('host_command')
    }
  })

  it('projects Owner claim and Agent repair as their own actions instead of readiness', () => {
    expect(resolveSetupPrimaryAction(makeApp({ nextAction: 'claim_owner' }))).toBe('claim_owner')
    expect(resolveSetupPrimaryAction(makeApp({ nextAction: 'repair_agent' }))).toBe('repair_agent')
  })

  it('never turns Server work into a human step', () => {
    for (const nextAction of [
      'reconcile_create',
      'reconcile_delete',
      'create_agent_app',
      'wait_for_operation',
      'apply_manifest',
      'bind_connection',
      'deleted',
    ]) {
      expect(resolveSetupPrimaryAction(makeApp({ nextAction }))).toBe('waiting')
    }
  })

  it('reports a ready App without an action', () => {
    expect(resolveSetupPrimaryAction(makeApp({ nextAction: 'ready' }))).toBe('ready')
  })
})

describe('SetupPrimaryAction', () => {
  it('renders the install link as the single action', () => {
    render(
      <SetupPrimaryAction
        app={makeApp({ nextAction: 'approve_install', installUrl: 'https://slack.example/oauth' })}
        target={target}
      />,
    )

    const action = screen.getByTestId('connection-setup-primary-action')
    expect(action).toHaveAttribute('data-action', 'approve_install')
    expect(action).toHaveAttribute('href', 'https://slack.example/oauth')
    expect(screen.queryByTestId('connection-setup-host-command')).not.toBeInTheDocument()
  })

  it('renders the host command without any credential value', () => {
    render(<SetupPrimaryAction app={makeApp({ nextAction: 'provide_credentials' })} target={target} />)

    const action = screen.getByTestId('connection-setup-primary-action')
    expect(action).toHaveAttribute('data-action', 'host_command')
    expect(screen.getByTestId('connection-setup-host-command')).toHaveTextContent(
      'mo slack install-agent agent-1 --project Test',
    )
    expect(action).toHaveTextContent('--credentials-file')
    expect(action.textContent ?? '').not.toMatch(/xoxb-|xapp-|token\s*[:=]/i)
    expect(action.querySelector('a')).toBeNull()
  })

  it('keeps the Project, Agent, and Workspace target in the copied install command', () => {
    render(
      <SetupPrimaryAction
        app={makeApp({ nextAction: 'provide_credentials' })}
        target={{ agentId: 'agent-1', connectionId: 'conn-1', projectRef: 'Test', workspaceTeamId: 'T0001' }}
      />,
    )

    expect(screen.getByTestId('connection-setup-host-command')).toHaveTextContent(
      'mo slack install-agent agent-1 --project Test --workspace-team T0001',
    )
  })

  it('renders a waiting state for Server-owned work', () => {
    render(<SetupPrimaryAction app={makeApp({ nextAction: 'reconcile_create' })} target={target} />)

    const action = screen.getByTestId('connection-setup-primary-action')
    expect(action).toHaveAttribute('data-action', 'waiting')
    expect(action.textContent ?? '').not.toMatch(/reconcile/i)
    expect(action.querySelector('a')).toBeNull()
    expect(screen.queryByTestId('connection-setup-host-command')).not.toBeInTheDocument()
  })

  it('hands the Owner claim to the host command and names the Bot DM destination', () => {
    render(<SetupPrimaryAction app={makeApp({ nextAction: 'claim_owner' })} target={target} botName="Writer" />)

    const action = screen.getByTestId('connection-setup-primary-action')
    expect(action).toHaveAttribute('data-action', 'claim_owner')
    expect(screen.getByTestId('connection-setup-host-command')).toHaveTextContent(
      'mo slack claim-owner conn-1 --project Test',
    )
    expect(screen.getByTestId('connection-setup-claim-destination')).toHaveTextContent(
      'direct message with the writer bot',
    )
    expect(action.querySelector('button')).toBeNull()
  })

  it('never renders a claim code on the claim action', () => {
    render(<SetupPrimaryAction app={makeApp({ nextAction: 'claim_owner' })} target={target} botName="Writer" />)

    expect(screen.queryByTestId('connection-setup-claim-owner-code')).not.toBeInTheDocument()
    expect(screen.queryByTestId('connection-setup-claim-owner-generate')).not.toBeInTheDocument()
    const action = screen.getByTestId('connection-setup-primary-action')
    expect(action.querySelector('button, input, textarea')).toBeNull()
  })

  it('names the Bot DM destination even before Slack verified the identity', () => {
    render(<SetupPrimaryAction app={makeApp({ nextAction: 'claim_owner' })} target={target} botName={null} />)

    expect(screen.getByTestId('connection-setup-claim-destination')).toHaveTextContent(
      'direct message with this Connection',
    )
  })

  it('points Agent repair at the existing Agent repair surface', () => {
    render(
      <SetupPrimaryAction
        app={makeApp({ nextAction: 'repair_agent' })}
        target={target}
        agentRepair={{ label: 'Agent settings', href: '/Test/agents/agent-1' }}
      />,
    )

    const action = screen.getByTestId('connection-setup-primary-action')
    expect(action).toHaveAttribute('data-action', 'repair_agent')
    const link = screen.getByTestId('connection-setup-agent-repair-link')
    expect(link).toHaveAttribute('href', '/Test/agents/agent-1')
    expect(link).toHaveTextContent('Agent settings')
  })

  it('omits the project reference when the page has no resolved Project', () => {
    render(
      <SetupPrimaryAction
        app={makeApp({ nextAction: 'claim_owner' })}
        target={{ agentId: 'agent-1', connectionId: 'conn-1', projectRef: null }}
        botName="Writer Bot"
      />,
    )

    expect(screen.getByTestId('connection-setup-host-command')).toHaveTextContent('mo slack claim-owner conn-1')
    expect(screen.getByTestId('connection-setup-host-command')).not.toHaveTextContent('--project')
  })
})

describe('botDmDestination', () => {
  it('names the Bot identity the claim code is sent to', () => {
    expect(botDmDestination('Writer')).toBe('Direct message with the Writer Bot')
    expect(botDmDestination(null)).toBeNull()
  })
})
