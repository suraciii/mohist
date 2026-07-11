import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { RuntimeDecisionSurface, type RuntimeDecisionSurfaceMutations } from './RuntimeDecisionSurface'
import type { RuntimeDecision } from '../model/derive-runtime-decision'

function mutation<TMutation extends { mutate: unknown; isPending: boolean; error: Error | null } = RuntimeDecisionSurfaceMutations['startMutation']>(overrides: Partial<TMutation> = {}): TMutation {
  return {
    mutate: vi.fn() as TMutation['mutate'],
    isPending: false,
    error: null,
    ...overrides,
  } as TMutation
}

function mutations(overrides: Partial<RuntimeDecisionSurfaceMutations> = {}): RuntimeDecisionSurfaceMutations {
  return {
    approveMutation: mutation(),
    sendBackMutation: mutation<RuntimeDecisionSurfaceMutations['sendBackMutation']>(),
    retryMutation: mutation(),
    resumeMutation: mutation(),
    rerunMutation: mutation(),
    forceStopMutation: mutation(),
    stopMutation: mutation(),
    startMutation: mutation(),
    ...overrides,
  }
}

function decision(overrides: Partial<RuntimeDecision> = {}): RuntimeDecision {
  const stop = { kind: 'stop' as const, label: 'Stop', enabled: true }
  return {
    summary: 'running',
    headline: 'Workflow running (Build)',
    rationale: 'The workflow is currently executing.',
    currentTask: null,
    nextAction: 'No user action required right now.',
    primary: stop,
    actions: [stop, { kind: 'inspect', label: 'View transcript', enabled: false }],
    stopRecoverable: true,
    waitReason: null,
    driftNote: null,
    blockedReason: null,
    approvalStage: null,
    ...overrides,
  }
}

describe('RuntimeDecisionSurface', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('renders exactly one primary action from the supplied decision', () => {
    render(<RuntimeDecisionSurface decision={decision()} mutations={mutations()} />)

    const surface = screen.getByTestId('runtime-decision-surface')
    expect(surface.dataset.summary).toBe('running')
    expect(surface.className).toContain('bg-card')
    expect(surface.className).toContain('border-l-info')

    const primaryActions = within(surface).getAllByRole('button')
      .filter((button) => button.getAttribute('data-primary') === 'true')
    expect(primaryActions).toHaveLength(1)
    expect(primaryActions[0]).toHaveAttribute('data-testid', 'runtime-action-stop')
  })

  it('uses shared pending state for the matching primary action', () => {
    render(
      <RuntimeDecisionSurface
        decision={decision()}
        mutations={mutations({ forceStopMutation: mutation<RuntimeDecisionSurfaceMutations['forceStopMutation']>({ isPending: true }) })}
      />,
    )

    const stop = screen.getByTestId('runtime-action-stop')
    expect(stop).toBeDisabled()
    expect(stop).toHaveTextContent('Stopping...')
  })

  it('routes a recoverable Stop through forceStopMutation after confirmation copy is shown', () => {
    const forceStopMutation = mutation()
    const stopMutation = mutation()
    render(
      <RuntimeDecisionSurface
        decision={decision({ stopRecoverable: true })}
        mutations={mutations({ forceStopMutation, stopMutation })}
      />,
    )

    const stop = screen.getByTestId('runtime-action-stop')
    fireEvent.click(stop)
    expect(screen.getByTestId('runtime-stop-confirmation-copy')).toHaveTextContent('preserve progress')
    expect(forceStopMutation.mutate).not.toHaveBeenCalled()

    fireEvent.click(stop)
    expect(forceStopMutation.mutate).toHaveBeenCalledTimes(1)
    expect(stopMutation.mutate).not.toHaveBeenCalled()
  })

  it('routes a terminal Stop through stopMutation after irreversible confirmation copy is shown', () => {
    const forceStopMutation = mutation()
    const stopMutation = mutation()
    render(
      <RuntimeDecisionSurface
        decision={decision({ stopRecoverable: false })}
        mutations={mutations({ forceStopMutation, stopMutation })}
      />,
    )

    const stop = screen.getByTestId('runtime-action-stop')
    fireEvent.click(stop)
    expect(screen.getByTestId('runtime-stop-confirmation-copy')).toHaveTextContent('irreversible')

    fireEvent.click(stop)
    expect(stopMutation.mutate).toHaveBeenCalledTimes(1)
    expect(forceStopMutation.mutate).not.toHaveBeenCalled()
  })

  it('shows stop consequence copy when Stop is a secondary visible action', () => {
    const stop = { kind: 'stop' as const, label: 'Stop', enabled: true }
    const retry = { kind: 'retry' as const, label: 'Retry', enabled: true }
    render(
      <RuntimeDecisionSurface
        decision={decision({
          summary: 'blocked',
          primary: retry,
          actions: [retry, stop],
          stopRecoverable: true,
        })}
        mutations={mutations()}
      />,
    )

    fireEvent.click(screen.getByTestId('runtime-action-stop'))

    expect(screen.getByTestId('runtime-stop-confirmation-copy')).toHaveTextContent('preserve progress')
  })

  it('renders inspect as disabled even if a decision accidentally marks it enabled', () => {
    const inspect = { kind: 'inspect' as const, label: 'View transcript', enabled: true }
    render(
      <RuntimeDecisionSurface
        decision={decision({ primary: null, actions: [inspect] })}
        mutations={mutations()}
      />,
    )

    const inspectButton = screen.getByTestId('runtime-action-inspect')
    expect(inspectButton).toBeDisabled()
    expect(inspectButton.getAttribute('title')).toMatch(/transcript navigation/i)
  })

  it('collects feedback text before sending back an approval', () => {
    const sendBackMutation = mutation<RuntimeDecisionSurfaceMutations['sendBackMutation']>()
    const sendBack = { kind: 'send-back' as const, label: 'Send back', enabled: true }
    render(
      <RuntimeDecisionSurface
        decision={decision({
          summary: 'approval-required',
          primary: sendBack,
          actions: [sendBack],
          approvalStage: 'check',
        })}
        mutations={mutations({ sendBackMutation })}
      />,
    )

    fireEvent.click(screen.getByTestId('runtime-action-send-back'))

    expect(screen.getByTestId('runtime-send-back-form')).toBeInTheDocument()
    expect(screen.getByTestId('runtime-submit-send-back')).toBeDisabled()
    fireEvent.change(screen.getByTestId('runtime-send-back-textarea'), {
      target: { value: 'Please address the verification failure.' },
    })
    fireEvent.click(screen.getByTestId('runtime-submit-send-back'))

    expect(sendBackMutation.mutate).toHaveBeenCalledWith(
      { stage: 'check', body: 'Please address the verification failure.' },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    )
  })
})
