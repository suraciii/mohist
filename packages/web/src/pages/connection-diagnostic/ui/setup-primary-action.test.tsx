import '@testing-library/jest-dom'
import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { SetupPrimaryAction, resolveSetupPrimaryAction } from './setup-primary-action'
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
        agentId="agent-1"
      />,
    )

    const action = screen.getByTestId('connection-setup-primary-action')
    expect(action).toHaveAttribute('data-action', 'approve_install')
    expect(action).toHaveAttribute('href', 'https://slack.example/oauth')
    expect(screen.queryByTestId('connection-setup-host-command')).not.toBeInTheDocument()
  })

  it('renders the host command without any credential value', () => {
    render(<SetupPrimaryAction app={makeApp({ nextAction: 'provide_credentials' })} agentId="agent-1" />)

    const action = screen.getByTestId('connection-setup-primary-action')
    expect(action).toHaveAttribute('data-action', 'host_command')
    expect(screen.getByTestId('connection-setup-host-command')).toHaveTextContent('mo slack install-agent agent-1')
    expect(action.textContent ?? '').not.toMatch(/xoxb-|xapp-|token\s*[:=]/i)
    expect(action.querySelector('a')).toBeNull()
  })

  it('renders a waiting state for Server-owned work', () => {
    render(<SetupPrimaryAction app={makeApp({ nextAction: 'reconcile_create' })} agentId="agent-1" />)

    const action = screen.getByTestId('connection-setup-primary-action')
    expect(action).toHaveAttribute('data-action', 'waiting')
    expect(action.textContent ?? '').not.toMatch(/reconcile/i)
    expect(action.querySelector('a')).toBeNull()
    expect(screen.queryByTestId('connection-setup-host-command')).not.toBeInTheDocument()
  })
})
