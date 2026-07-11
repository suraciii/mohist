import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { MobileActionBar } from './MobileActionBar'
import type { RuntimeDecision, RuntimeAvailableAction } from '../../../widgets/issue-workflow/model/derive-runtime-decision'
import type { RuntimeDecisionSurfaceMutations } from '../../../widgets/issue-workflow/ui/RuntimeDecisionSurface'

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
  const stop: RuntimeAvailableAction = { kind: 'stop', label: 'Stop', enabled: true }
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

describe('MobileActionBar', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('renders nothing when decision.primary is null', () => {
    const { container } = render(
      <MobileActionBar
        decision={decision({ primary: null, actions: [] })}
        mutations={mutations()}
      />,
    )
    expect(container.firstChild).toBeNull()
  })

  it('renders a single primary action button sourced from decision.primary', () => {
    const stop: RuntimeAvailableAction = { kind: 'stop', label: 'Stop', enabled: true }
    render(<MobileActionBar decision={decision({ primary: stop })} mutations={mutations()} />)

    const bar = screen.getByTestId('mobile-action-bar')
    expect(bar.dataset.actionKind).toBe('stop')
    expect(bar.dataset.summary).toBe('running')

    const primaryButton = screen.getByTestId('mobile-action-stop')
    expect(primaryButton).toHaveAttribute('data-primary', 'true')
    expect(within(bar).queryByTestId('mobile-action-send-back')).toBeNull()
    expect(within(bar).queryByTestId('mobile-action-approve')).toBeNull()
  })

  it('uses the Stop copy verbatim from decision.primary label', () => {
    render(<MobileActionBar decision={decision()} mutations={mutations()} />)
    expect(screen.getByTestId('mobile-action-stop')).toHaveTextContent('Stop')
  })

  it('preserves custom primary labels from decision.primary', () => {
    const start: RuntimeAvailableAction = { kind: 'start', label: 'Start new workflow', enabled: true }
    render(
      <MobileActionBar
        decision={decision({
          summary: 'failed',
          primary: start,
          actions: [start],
          stopRecoverable: null,
        })}
        mutations={mutations()}
      />,
    )

    expect(screen.getByTestId('mobile-action-start')).toHaveTextContent('Start new workflow')
  })

  it('disables unavailable primary actions, exposes the reason, and does not invoke mutations', () => {
    const start: RuntimeAvailableAction = {
      kind: 'start',
      label: 'Start',
      enabled: false,
      reason: 'Issue is still a draft. Mark it ready before starting.',
    }
    const startMutation = mutation()
    render(
      <MobileActionBar
        decision={decision({
          summary: 'queued',
          primary: start,
          actions: [start],
          stopRecoverable: null,
        })}
        mutations={mutations({ startMutation })}
      />,
    )

    const button = screen.getByTestId('mobile-action-start')
    expect(button).toBeDisabled()
    expect(button).toHaveTextContent('Start')
    expect(button).toHaveAttribute('title', 'Issue is still a draft. Mark it ready before starting.')
    expect(button).toHaveAttribute('aria-describedby', 'mobile-action-start-reason')

    fireEvent.click(button)

    expect(startMutation.mutate).not.toHaveBeenCalled()
  })

  it('opens the confirmation drawer when a destructive primary (Stop) is activated', () => {
    render(<MobileActionBar decision={decision()} mutations={mutations()} />)

    expect(screen.queryByTestId('confirmation-drawer')).toBeNull()

    fireEvent.click(screen.getByTestId('mobile-action-stop'))

    const drawer = screen.getByTestId('confirmation-drawer')
    expect(drawer).toBeInTheDocument()
    expect(drawer).toHaveAttribute('role', 'dialog')
    expect(drawer).toHaveAttribute('aria-modal', 'true')
    expect(screen.getByTestId('mobile-stop-confirmation')).toBeInTheDocument()
    expect(screen.getByTestId('mobile-confirmation-title')).toHaveTextContent('recoverable')
  })

  it('renders recoverable consequence copy in the stop drawer when stopRecoverable is true', () => {
    render(
      <MobileActionBar
        decision={decision({ stopRecoverable: true })}
        mutations={mutations()}
      />,
    )

    fireEvent.click(screen.getByTestId('mobile-action-stop'))

    expect(screen.getByTestId('mobile-confirmation-body')).toHaveTextContent('preserve progress')
  })

  it('renders irreversible consequence copy in the stop drawer when stopRecoverable is false', () => {
    render(
      <MobileActionBar
        decision={decision({ stopRecoverable: false })}
        mutations={mutations()}
      />,
    )

    fireEvent.click(screen.getByTestId('mobile-action-stop'))

    expect(screen.getByTestId('mobile-confirmation-body')).toHaveTextContent('irreversible')
  })

  it('confirming a recoverable Stop routes to forceStopMutation', () => {
    const forceStopMutation = mutation()
    const stopMutation = mutation()
    render(
      <MobileActionBar
        decision={decision({ stopRecoverable: true })}
        mutations={mutations({ forceStopMutation, stopMutation })}
      />,
    )

    fireEvent.click(screen.getByTestId('mobile-action-stop'))
    fireEvent.click(screen.getByTestId('mobile-confirmation-confirm'))

    expect(forceStopMutation.mutate).toHaveBeenCalledTimes(1)
    expect(stopMutation.mutate).not.toHaveBeenCalled()
  })

  it('confirming an irreversible Stop routes to stopMutation', () => {
    const forceStopMutation = mutation()
    const stopMutation = mutation()
    render(
      <MobileActionBar
        decision={decision({ stopRecoverable: false })}
        mutations={mutations({ forceStopMutation, stopMutation })}
      />,
    )

    fireEvent.click(screen.getByTestId('mobile-action-stop'))
    fireEvent.click(screen.getByTestId('mobile-confirmation-confirm'))

    expect(stopMutation.mutate).toHaveBeenCalledTimes(1)
    expect(forceStopMutation.mutate).not.toHaveBeenCalled()
  })

  it('does not invoke any mutation when the stop drawer Cancel is pressed', () => {
    const forceStopMutation = mutation()
    const stopMutation = mutation()
    render(
      <MobileActionBar
        decision={decision({ stopRecoverable: true })}
        mutations={mutations({ forceStopMutation, stopMutation })}
      />,
    )

    fireEvent.click(screen.getByTestId('mobile-action-stop'))
    fireEvent.click(screen.getByTestId('mobile-confirmation-cancel'))

    expect(forceStopMutation.mutate).not.toHaveBeenCalled()
    expect(stopMutation.mutate).not.toHaveBeenCalled()
    expect(screen.queryByTestId('confirmation-drawer')).toBeNull()
  })

  it('invokes start mutation immediately when primary is Start and not destructive', () => {
    const start: RuntimeAvailableAction = { kind: 'start', label: 'Start', enabled: true }
    const startMutation = mutation()
    render(
      <MobileActionBar
        decision={decision({
          summary: 'queued',
          primary: start,
          actions: [start],
          stopRecoverable: null,
        })}
        mutations={mutations({ startMutation })}
      />,
    )

    fireEvent.click(screen.getByTestId('mobile-action-start'))

    expect(startMutation.mutate).toHaveBeenCalledTimes(1)
    expect(screen.queryByTestId('confirmation-drawer')).toBeNull()
  })

  it('invokes approve mutation immediately when primary is Approve and not destructive', () => {
    const approve: RuntimeAvailableAction = { kind: 'approve', label: 'Approve', enabled: true }
    const approveMutation = mutation()
    render(
      <MobileActionBar
        decision={decision({
          summary: 'approval-required',
          primary: approve,
          actions: [approve, { kind: 'send-back', label: 'Send back', enabled: true }],
          approvalStage: 'check',
        })}
        mutations={mutations({ approveMutation })}
      />,
    )

    fireEvent.click(screen.getByTestId('mobile-action-approve'))

    expect(approveMutation.mutate).toHaveBeenCalledTimes(1)
    expect(screen.queryByTestId('confirmation-drawer')).toBeNull()
  })

  it('opens the send-back drawer when Send back is the primary', () => {
    const sendBack: RuntimeAvailableAction = { kind: 'send-back', label: 'Send back', enabled: true }
    render(
      <MobileActionBar
        decision={decision({
          summary: 'approval-required',
          primary: sendBack,
          actions: [{ kind: 'approve', label: 'Approve', enabled: true }, sendBack],
          approvalStage: 'check',
        })}
        mutations={mutations()}
      />,
    )

    fireEvent.click(screen.getByTestId('mobile-action-send-back'))

    expect(screen.getByTestId('mobile-send-back-form')).toBeInTheDocument()
    expect(screen.getByTestId('mobile-send-back-textarea')).toBeInTheDocument()
  })

  it('does not call sendBackMutation until the textarea has content', () => {
    const sendBack: RuntimeAvailableAction = { kind: 'send-back', label: 'Send back', enabled: true }
    const sendBackMutation = mutation<RuntimeDecisionSurfaceMutations['sendBackMutation']>()
    render(
      <MobileActionBar
        decision={decision({
          summary: 'approval-required',
          primary: sendBack,
          actions: [{ kind: 'approve', label: 'Approve', enabled: true }, sendBack],
          approvalStage: 'check',
        })}
        mutations={mutations({ sendBackMutation })}
      />,
    )

    fireEvent.click(screen.getByTestId('mobile-action-send-back'))

    const submit = screen.getByTestId('mobile-confirmation-confirm')
    expect(submit).toBeDisabled()

    fireEvent.change(screen.getByTestId('mobile-send-back-textarea'), {
      target: { value: 'Please fix the failing test.' },
    })
    expect(submit).not.toBeDisabled()

    fireEvent.click(submit)

    expect(sendBackMutation.mutate).toHaveBeenCalledWith(
      { stage: 'check', body: 'Please fix the failing test.' },
      expect.objectContaining({ onSuccess: expect.any(Function) }),
    )
  })

  it('uses fixed positioning and CSS offsets that clear the mobile nav', () => {
    render(<MobileActionBar decision={decision()} mutations={mutations()} />)

    const bar = screen.getByTestId('mobile-action-bar')
    expect(bar.className).toMatch(/fixed\b/)
    expect(bar.className).toMatch(/inset-x-0\b/)
    expect(bar.className).toMatch(/\bz-30\b/)
    expect(bar.className).toMatch(/bottom-\[calc\(/)
    expect(bar.className).toMatch(/md:bottom-0/)
  })

  it('renders immediate action mutation errors in an alert region', () => {
    const start: RuntimeAvailableAction = { kind: 'start', label: 'Start', enabled: true }
    render(
      <MobileActionBar
        decision={decision({
          summary: 'queued',
          primary: start,
          actions: [start],
          stopRecoverable: null,
        })}
        mutations={mutations({
          startMutation: mutation<RuntimeDecisionSurfaceMutations['startMutation']>({
            error: new Error('Start failed'),
          }),
        })}
      />,
    )

    const error = screen.getByTestId('mobile-action-error')
    expect(error).toHaveAttribute('role', 'alert')
    expect(error).toHaveAttribute('aria-live', 'polite')
    expect(error).toHaveTextContent('Start failed')
  })

  it('renders drawer-confirmed mutation errors inside the open drawer', () => {
    const sendBack: RuntimeAvailableAction = { kind: 'send-back', label: 'Send back', enabled: true }
    const baseDecision = decision({
      summary: 'approval-required',
      primary: sendBack,
      actions: [{ kind: 'approve', label: 'Approve', enabled: true }, sendBack],
      approvalStage: 'check',
    })
    const sendBackMutation = mutation<RuntimeDecisionSurfaceMutations['sendBackMutation']>()
    const { rerender } = render(
      <MobileActionBar
        decision={baseDecision}
        mutations={mutations({ sendBackMutation })}
      />,
    )

    fireEvent.click(screen.getByTestId('mobile-action-send-back'))
    fireEvent.change(screen.getByTestId('mobile-send-back-textarea'), {
      target: { value: 'Please revise this.' },
    })
    fireEvent.click(screen.getByTestId('mobile-confirmation-confirm'))

    rerender(
      <MobileActionBar
        decision={baseDecision}
        mutations={mutations({
          sendBackMutation: mutation<RuntimeDecisionSurfaceMutations['sendBackMutation']>({
            error: new Error('Send back failed'),
          }),
        })}
      />,
    )

    const drawer = screen.getByTestId('confirmation-drawer')
    const error = within(drawer).getByTestId('mobile-action-error')
    expect(error).toHaveAttribute('role', 'alert')
    expect(error).toHaveTextContent('Send back failed')
  })

  it('disables the primary button while a mutation is pending and shows pending copy', () => {
    const forceStopMutation = mutation<RuntimeDecisionSurfaceMutations['forceStopMutation']>({ isPending: true })
    render(
      <MobileActionBar
        decision={decision({ stopRecoverable: true })}
        mutations={mutations({ forceStopMutation })}
      />,
    )

    const button = screen.getByTestId('mobile-action-stop')
    expect(button).toBeDisabled()
    expect(button).toHaveTextContent('Stopping...')
  })
})
