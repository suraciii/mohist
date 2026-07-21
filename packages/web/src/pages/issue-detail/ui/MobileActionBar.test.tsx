import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { MobileActionBar } from './MobileActionBar'
import type { IssueDecisionAction, IssueDecisionActionKind } from '../model/issueDecisionActions'
import type { IssueDecisionActionController } from '../model/useIssueDecisionActions'

function makeAction(kind: IssueDecisionActionKind, overrides: Partial<IssueDecisionAction> = {}): IssueDecisionAction {
  return {
    kind,
    label: kind,
    pendingLabel: 'Pending...',
    enabled: true,
    reason: null,
    primary: false,
    destructive: false,
    mode: 'immediate',
    to: null,
    order: 0,
    ...overrides,
  }
}

function buildController(overrides: Partial<IssueDecisionActionController> = {}): IssueDecisionActionController {
  return {
    pendingKind: null,
    error: null,
    stopConfirming: false,
    stopConfirmTitle: 'Stop (recoverable)',
    stopConfirmBody: 'Stop will preserve progress.',
    openStopConfirm: vi.fn(),
    closeStopConfirm: vi.fn(),
    runAction: vi.fn(),
    sendBackBodyValid: vi.fn(() => true),
    ...overrides,
  }
}

function renderBar(props: Partial<React.ComponentProps<typeof MobileActionBar>> = {}) {
  return render(
    <MemoryRouter>
      <MobileActionBar
        actions={props.actions ?? [makeAction('stop')]}
        primary={'primary' in props ? (props.primary as IssueDecisionAction | null) : makeAction('stop')}
        rationale={props.rationale ?? 'The workflow is currently executing.'}
        nextAction={props.nextAction ?? 'No action required right now.'}
        controller={props.controller ?? buildController()}
        summary={props.summary ?? 'running'}
      />
    </MemoryRouter>,
  )
}

afterEach(() => cleanup())

describe('MobileActionBar', () => {
  it('keeps no-action context reachable from the launcher', () => {
    renderBar({ actions: [], primary: null })

    fireEvent.click(screen.getByTestId('mobile-action-sheet-launcher'))
    expect(screen.getByTestId('mobile-action-sheet-rationale')).toHaveTextContent('The workflow is currently executing.')
    expect(screen.getByTestId('mobile-action-sheet-next-action')).toHaveTextContent('No action required right now.')
    expect(screen.getByTestId('mobile-sheet-no-action')).toBeInTheDocument()
  })

  it('renders the launcher with the primary label', () => {
    renderBar({ primary: makeAction('stop', { label: 'Stop workflow' }) })
    const launcher = screen.getByTestId('mobile-action-sheet-launcher')
    expect(launcher).toHaveTextContent('Stop workflow')
    expect(launcher).toHaveAttribute('data-action-kind', 'stop')
  })

  it('keeps the launcher enabled even when the primary action itself is disabled', () => {
    renderBar({
      primary: makeAction('start', { label: 'Start', enabled: false, reason: 'Mark the issue ready before starting.' }),
      actions: [makeAction('start', { enabled: false, reason: 'Mark the issue ready before starting.' })],
    })
    const launcher = screen.getByTestId('mobile-action-sheet-launcher')
    expect(launcher).not.toBeDisabled()
  })

  it('opens the action sheet containing every applicable action and the rationale + next-action text', () => {
    const approve = makeAction('approve', { label: 'Approve', primary: true, order: 0 })
    const sendBack = makeAction('send-back', { label: 'Send back', mode: 'feedback', order: 1 })
    const askAgent = makeAction('ask-agent', { label: 'Ask Agent', mode: 'navigation', to: '/agent-sessions/new?issue=14', order: 2 })
    renderBar({
      primary: approve,
      actions: [approve, sendBack, askAgent],
      rationale: 'The workflow is paused while an approval decision is pending.',
      nextAction: 'An approval decision is needed to continue.',
    })
    fireEvent.click(screen.getByTestId('mobile-action-sheet-launcher'))
    const sheet = screen.getByTestId('mobile-action-sheet')
    expect(within(sheet).getByTestId('mobile-action-sheet-rationale')).toHaveTextContent(/approval decision is pending/i)
    expect(within(sheet).getByTestId('mobile-action-sheet-next-action')).toHaveTextContent(/approval decision is needed to continue/i)
    expect(within(sheet).getByTestId('mobile-sheet-action-approve')).toBeTruthy()
    expect(within(sheet).getByTestId('mobile-sheet-action-send-back')).toBeTruthy()
    expect(within(sheet).getByTestId('mobile-sheet-action-ask-agent')).toBeTruthy()
  })

  it('renders disabled destructive actions without live destructive styling', () => {
    renderBar({
      primary: makeAction('stop', { enabled: false, reason: 'Stop becomes available between tasks.', destructive: true }),
      actions: [makeAction('stop', { enabled: false, reason: 'Stop becomes available between tasks.', destructive: true })],
    })
    fireEvent.click(screen.getByTestId('mobile-action-sheet-launcher'))
    const stopButton = screen.getByTestId('mobile-sheet-action-stop')
    expect(stopButton).toBeDisabled()
    expect(stopButton).toHaveAttribute('data-destructive', 'true')
    // the disabled treatment uses neutral muted styling instead of destructive background
    expect(stopButton.className).toContain('bg-muted')
    expect(stopButton.className).toContain('text-muted-foreground')
    expect(stopButton.className).not.toContain('text-destructive')
  })

  it('exposes a visible reason for every disabled action via aria-describedby', () => {
    renderBar({
      primary: makeAction('start', { enabled: false, reason: 'Mark the issue ready before starting.' }),
      actions: [makeAction('start', { enabled: false, reason: 'Mark the issue ready before starting.' })],
    })
    fireEvent.click(screen.getByTestId('mobile-action-sheet-launcher'))
    const start = screen.getByTestId('mobile-sheet-action-start')
    expect(start).toHaveAttribute('aria-describedby', 'mobile-sheet-action-start-reason')
    const reason = screen.getByTestId('mobile-sheet-action-start-reason')
    expect(reason).toHaveTextContent('Mark the issue ready before starting.')
  })

  it('locks every action with a visible associated reason while a mutation is in flight', () => {
    renderBar({
      primary: makeAction('approve', { pendingLabel: 'Approving...', primary: true }),
      actions: [
        makeAction('approve', { pendingLabel: 'Approving...', primary: true }),
        makeAction('send-back', { label: 'Send back' }),
      ],
      controller: buildController({ pendingKind: 'approve' }),
    })
    fireEvent.click(screen.getByTestId('mobile-action-sheet-launcher'))
    const approve = screen.getByTestId('mobile-sheet-action-approve')
    expect(approve).toBeDisabled()
    expect(approve).toHaveTextContent('Approving...')
    expect(approve).toHaveAttribute('aria-describedby', 'mobile-sheet-action-approve-reason')
    expect(screen.getByTestId('mobile-sheet-action-approve-reason')).toHaveTextContent(/another request is in progress/i)
    const sendBack = screen.getByTestId('mobile-sheet-action-send-back')
    expect(sendBack).toBeDisabled()
    expect(sendBack).toHaveAttribute('aria-describedby', 'mobile-sheet-action-send-back-reason')
    expect(screen.getByTestId('mobile-sheet-action-send-back-reason')).toHaveTextContent(/another request is in progress/i)
    const pending = screen.getByTestId('mobile-sheet-action-approve-pending')
    expect(pending).toHaveAttribute('aria-live', 'polite')
    expect(pending.textContent ?? '').toMatch(/another request/i)
  })

  it('calls controller.runAction when an enabled action button is clicked', () => {
    const runAction = vi.fn()
    renderBar({
      primary: makeAction('approve', { primary: true }),
      actions: [makeAction('approve', { primary: true })],
      controller: buildController({ runAction }),
    })
    fireEvent.click(screen.getByTestId('mobile-action-sheet-launcher'))
    fireEvent.click(screen.getByTestId('mobile-sheet-action-approve'))
    expect(runAction).toHaveBeenCalledWith(expect.objectContaining({ kind: 'approve' }))
    expect(screen.getByTestId('mobile-action-sheet')).toBeInTheDocument()
  })

  it('opens the stop confirmation when stop is clicked and routes through runAction on confirm', () => {
    const openStopConfirm = vi.fn()
    const runAction = vi.fn()
    const controller = buildController({ openStopConfirm, runAction, stopConfirming: true })
    renderBar({
      primary: makeAction('stop', { mode: 'confirmation', destructive: true }),
      actions: [makeAction('stop', { mode: 'confirmation', destructive: true })],
      controller,
    })
    fireEvent.click(screen.getByTestId('mobile-action-sheet-launcher'))
    fireEvent.click(screen.getByTestId('mobile-sheet-action-stop'))
    expect(openStopConfirm).toHaveBeenCalledOnce()
    const sheet = screen.getByTestId('mobile-action-sheet')
    expect(within(sheet).getByTestId('mobile-stop-confirmation')).toBeInTheDocument()
    expect(within(sheet).getByTestId('mobile-stop-confirmation-title')).toHaveTextContent(/recoverable/i)
    fireEvent.click(within(sheet).getByTestId('mobile-stop-confirmation-confirm'))
    expect(runAction).toHaveBeenCalledWith(expect.objectContaining({ kind: 'stop' }))
  })

  it('collects feedback before sending back an approval', () => {
    const runAction = vi.fn()
    const sendBackBodyValid = vi.fn((body: string) => body.trim().length > 0)
    renderBar({
      primary: makeAction('approve', { primary: true }),
      actions: [
        makeAction('approve', { primary: true }),
        makeAction('send-back', { label: 'Send back', mode: 'feedback' }),
      ],
      controller: buildController({ runAction, sendBackBodyValid }),
    })
    fireEvent.click(screen.getByTestId('mobile-action-sheet-launcher'))
    fireEvent.click(screen.getByTestId('mobile-sheet-action-send-back'))
    const confirm = screen.getByTestId('mobile-send-back-confirm')
    expect(confirm).toBeDisabled()
    fireEvent.change(screen.getByTestId('mobile-send-back-textarea'), {
      target: { value: 'Tighten the failing test.' },
    })
    expect(confirm).not.toBeDisabled()
    fireEvent.click(confirm)
    expect(runAction).toHaveBeenCalledWith(
      expect.objectContaining({ kind: 'send-back' }),
      { sendBackBody: 'Tighten the failing test.' },
    )
  })

  it('closes the sheet via Escape', () => {
    const closeStopConfirm = vi.fn()
    renderBar({ controller: buildController({ closeStopConfirm }) })
    fireEvent.click(screen.getByTestId('mobile-action-sheet-launcher'))
    const drawer = screen.getByTestId('mobile-action-sheet')
    expect(drawer).toBeInTheDocument()
    fireEvent.keyDown(document, { key: 'Escape' })
    expect(screen.queryByTestId('mobile-action-sheet')).toBeNull()
  })

  it('renders an error message when the controller surfaces one', () => {
    renderBar({
      controller: buildController({ error: new Error('Approve failed') }),
    })
    fireEvent.click(screen.getByTestId('mobile-action-sheet-launcher'))
    const error = screen.getByTestId('mobile-action-error')
    expect(error).toHaveAttribute('role', 'alert')
    expect(error).toHaveTextContent('Approve failed')
  })

  it('renders a launcher primary label that uses pending copy when a runtime action is pending', () => {
    renderBar({
      primary: makeAction('stop', { label: 'Stop', pendingLabel: 'Stopping...' }),
      actions: [makeAction('stop', { label: 'Stop', pendingLabel: 'Stopping...' })],
      controller: buildController({ pendingKind: 'stop' }),
    })
    const launcher = screen.getByTestId('mobile-action-sheet-launcher')
    expect(launcher).toHaveTextContent('Stopping...')
  })
})
