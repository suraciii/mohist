import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { ApprovalReviewPackage, serializeSendBackFeedback, type SendBackDraft } from './ApprovalReviewPackage'
import type { IssueDecisionAction } from '../model/issueDecisionActions'
import type { IssueDecisionActionController } from '../model/useIssueDecisionActions'

const action = (kind: IssueDecisionAction['kind'], order: number, enabled = true): IssueDecisionAction => ({
  kind,
  label: kind === 'ask-agent' ? 'Ask Agent' : kind === 'view-transcript' ? 'View transcript · session-1' : kind === 'approve' ? 'Approve' : 'Send back',
  pendingLabel: 'Pending',
  enabled,
  reason: enabled ? null : 'Unavailable',
  primary: kind === 'approve',
  destructive: kind === 'send-back',
  mode: kind === 'send-back' ? 'feedback' : kind === 'ask-agent' || kind === 'view-transcript' ? 'navigation' : 'immediate',
  to: kind === 'ask-agent' ? '/agent-sessions/new' : kind === 'view-transcript' ? '/issues/455/workflow/sessions/session-1' : null,
  order,
})

const controller: IssueDecisionActionController = {
  pendingKind: null,
  error: null,
  stopConfirming: false,
  stopConfirmTitle: '',
  stopConfirmBody: '',
  openStopConfirm: vi.fn(),
  closeStopConfirm: vi.fn(),
  runAction: vi.fn(),
  sendBackBodyValid: () => true,
}

const artifactListHook = (_issue: number, params: { path?: string } = {}) => ({
  data: [{ artifactId: params.path ?? 'artifact', workflowRunId: 'run-1', taskRunId: 'task-1', path: params.path ?? '', kind: 'file' as const, recordedAt: '2026-01-01T00:00:00Z' }],
  isLoading: false,
  error: null,
})

const artifactContentHook = (_issue: number, artifactId: string | null) => ({
  data: { kind: 'text' as const, content: artifactId === 'tasks.json' ? '{"token":"' + 'x'.repeat(100) + '"}' : `# ${artifactId}`, contentType: artifactId === 'tasks.json' ? 'application/json' : 'text/markdown' },
  isLoading: false,
  error: null,
})

describe('ApprovalReviewPackage', () => {
  it('serializes one stable category and trimmed body', () => {
    const draft: SendBackDraft = { category: 'scope', body: '  Narrow this to the requested files.  ' }
    expect(serializeSendBackFeedback(draft)).toBe('Category: Scope\n\nNarrow this to the requested files.')
  })

  it('shows plan evidence and direct mobile actions without a generic launcher', () => {
    render(
      <MemoryRouter><ApprovalReviewPackage
        issueNumber={455}
        workflowRunId="run-1"
        approvalStage="plan"
        actions={[action('approve', 0), action('send-back', 1), action('ask-agent', 2), action('view-transcript', 3)]}
        controller={controller}
        rationale="Review the plan."
        nextAction="Approve or send back"
        isNarrowViewport
        artifactListHook={artifactListHook}
        artifactContentHook={artifactContentHook}
      /></MemoryRouter>,
    )

    expect(within(screen.getByTestId('approval-artifact-proposal.md')).getAllByText('proposal.md')).toHaveLength(2)
    expect(screen.getByText('tasks.json')).toBeInTheDocument()
    expect(screen.getByTestId('approval-mobile-approve')).toBeInTheDocument()
    expect(screen.getByTestId('approval-mobile-send-back')).toBeInTheDocument()
    expect(screen.queryByTestId('mobile-action-sheet-launcher')).not.toBeInTheDocument()
    expect(screen.queryByRole('menu')).not.toBeInTheDocument()

    fireEvent.click(screen.getByTestId('approval-mobile-send-back'))
    expect(screen.getByTestId('send-back-feedback-textarea')).toHaveFocus()
    fireEvent.click(screen.getByRole('radio', { name: 'Scope' }))
    fireEvent.change(screen.getByTestId('send-back-feedback-textarea'), { target: { value: 'Please narrow it.' } })
    fireEvent.click(screen.getByTestId('send-back-feedback-submit'))
    expect(controller.runAction).toHaveBeenCalledWith(expect.objectContaining({ kind: 'send-back' }), { sendBackBody: 'Category: Scope\n\nPlease narrow it.' })
  })

  it('keeps every secondary descriptor in ordered non-modal access', () => {
    render(
      <MemoryRouter><ApprovalReviewPackage
        issueNumber={455}
        workflowRunId="run-1"
        approvalStage="check"
        actions={[action('approve', 0), action('send-back', 1), action('ask-agent', 2), action('view-transcript', 3), action('close', 4, false)]}
        controller={controller}
        rationale="Review the check."
        nextAction="Approve or send back"
        isNarrowViewport
        artifactListHook={artifactListHook}
        artifactContentHook={artifactContentHook}
      /></MemoryRouter>,
    )
    fireEvent.click(screen.getByRole('button', { name: 'More actions' }))
    expect(screen.getByTestId('approval-more-action-ask-agent')).toBeInTheDocument()
    expect(screen.getByTestId('approval-more-action-view-transcript')).toBeInTheDocument()
    expect(screen.getByTestId('approval-more-action-close')).toBeDisabled()
    expect(screen.getByTestId('approval-mobile-approve')).toBeVisible()
    expect(screen.getByTestId('approval-mobile-send-back')).toBeVisible()
  })
})
