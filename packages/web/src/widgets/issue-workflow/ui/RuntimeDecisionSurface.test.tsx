// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { RuntimeDecisionSurface, type RuntimeDecisionSurfaceMutations } from './RuntimeDecisionSurface'
import type { RuntimeDecision } from '../model/derive-runtime-decision'

function mutation(overrides: Partial<RuntimeDecisionSurfaceMutations['startMutation']> = {}) {
  return {
    mutate: vi.fn() as unknown as RuntimeDecisionSurfaceMutations['startMutation']['mutate'],
    isPending: false,
    error: null,
    ...overrides,
  }
}

function mutations(overrides: Partial<RuntimeDecisionSurfaceMutations> = {}): RuntimeDecisionSurfaceMutations {
  return {
    approveMutation: mutation(),
    sendBackMutation: mutation(),
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
        mutations={mutations({ forceStopMutation: mutation({ isPending: true }) })}
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
})
