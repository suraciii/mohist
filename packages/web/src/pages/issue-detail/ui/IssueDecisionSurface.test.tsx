import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { IssueDecisionSurface } from './IssueDecisionSurface'
import type { IssueDecisionAction } from '../model/issueDecisionActions'
import type { IssueDecisionActionController } from '../model/useIssueDecisionActions'

const approve: IssueDecisionAction = {
  kind: 'approve',
  label: 'Approve',
  pendingLabel: 'Approving...',
  enabled: true,
  reason: null,
  primary: true,
  destructive: false,
  mode: 'immediate',
  to: null,
  order: 0,
}

const sendBack: IssueDecisionAction = {
  ...approve,
  kind: 'send-back',
  label: 'Send back',
  pendingLabel: 'Sending back...',
  primary: false,
  destructive: true,
  mode: 'feedback',
  order: 1,
}

const pendingController: IssueDecisionActionController = {
  pendingKind: 'approve',
  error: null,
  stopConfirming: false,
  stopConfirmTitle: 'Stop',
  stopConfirmBody: 'Stop the workflow.',
  openStopConfirm: vi.fn(),
  closeStopConfirm: vi.fn(),
  runAction: vi.fn(),
  sendBackBodyValid: vi.fn(),
}

describe('IssueDecisionSurface', () => {
  it('locks every action and renders visible busy reasons while a mutation is pending', () => {
    render(
      <IssueDecisionSurface
        actions={[approve, sendBack]}
        summary="approval-required"
        rationale="An approval decision is pending."
        nextAction="Wait for the request to finish."
        controller={pendingController}
      />,
    )

    const approving = screen.getByTestId('decision-action-approve')
    expect(approving).toBeDisabled()
    expect(approving).toHaveTextContent('Approving...')
    expect(approving).toHaveAttribute('aria-describedby', 'decision-action-approve-reason')
    expect(screen.getByTestId('decision-action-approve-reason')).toHaveTextContent(/another request is in progress/i)

    const sendingBack = screen.getByTestId('decision-action-send-back')
    expect(sendingBack).toBeDisabled()
    expect(sendingBack).toHaveAttribute('aria-describedby', 'decision-action-send-back-reason')
    expect(screen.getByTestId('decision-action-send-back-reason')).toHaveTextContent(/another request is in progress/i)
  })

  it('renders recoverable interruption as a warning recovery state', () => {
    render(
      <IssueDecisionSurface
        actions={[]}
        summary="recoverable-interrupted"
        rationale="The runner was interrupted."
        nextAction="Wait for recovery."
        controller={pendingController}
      />,
    )

    const surface = screen.getByTestId('issue-decision-surface')
    expect(surface).toHaveAttribute('data-summary', 'recoverable-interrupted')
    expect(surface).toHaveAttribute('data-tone', 'amber')
  })
})
