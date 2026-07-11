import { describe, expect, it } from 'vitest'
import { screen } from '@testing-library/react'
import { render } from '../../../../tests/test-utils'
import { FeedbackHistory } from './FeedbackHistory'
import type { ApprovalFeedback } from '../../../entities/issue'
import { WorkflowStage } from '../../../entities/issue'

function makeFeedback(overrides: Partial<ApprovalFeedback> = {}): ApprovalFeedback {
  return {
    id: 'fb-1',
    issueNumber: 1,
    workflowRunId: 'wr-1',
    stage: 'plan',
    status: 'open',
    body: 'Please address the issue',
    createdAt: '2026-01-01T00:00:00.000Z',
    resolution: null,
    ...overrides,
  }
}

describe('FeedbackHistory', () => {
  it('renders nothing when feedback array is empty', () => {
    const { container } = render(<FeedbackHistory stage={WorkflowStage.Plan} feedback={[]} />)
    expect(container.firstChild).toBeNull()
  })

  it('renders the Feedback history heading when feedback exists', () => {
    render(<FeedbackHistory stage={WorkflowStage.Plan} feedback={[makeFeedback()]} />)
    expect(screen.getByText('Feedback history')).toBeInTheDocument()
  })

  it('renders a single feedback cycle with body', () => {
    render(
      <FeedbackHistory
        stage={WorkflowStage.Plan}
        feedback={[makeFeedback({ id: 'fb-1', body: 'Fix the test' })]}
      />,
    )
    expect(screen.getByText('Cycle 1')).toBeInTheDocument()
    expect(screen.getByText('Fix the test')).toBeInTheDocument()
    expect(screen.getAllByText('Awaiting application').length).toBeGreaterThan(0)
    expect(screen.getByTestId('feedback-fb-1')).toBeInTheDocument()
  })

  it('renders resolution summary for resolved feedback', () => {
    render(
      <FeedbackHistory
        stage={WorkflowStage.Plan}
        feedback={[
          makeFeedback({
            id: 'fb-1',
            status: 'resolved',
            body: 'Add tests',
            resolution: {
              resolvedAt: '2026-01-01T00:10:00.000Z',
              resolutionTaskId: 'task-42',
              resolutionSummary: 'Added unit tests in tests/ directory',
            },
          }),
        ]}
      />,
    )
    expect(screen.getByText('Resolved')).toBeInTheDocument()
    expect(screen.getByText('Added unit tests in tests/ directory')).toBeInTheDocument()
    expect(screen.getByText('Resolution summary')).toBeInTheDocument()
    expect(screen.getByText('Feedback task applied')).toBeInTheDocument()
  })

  it('shows fallback when resolved feedback has no resolution summary', () => {
    render(
      <FeedbackHistory
        stage={WorkflowStage.Plan}
        feedback={[
          makeFeedback({
            id: 'fb-1',
            status: 'resolved',
            resolution: {
              resolvedAt: '2026-01-01T00:10:00.000Z',
              resolutionSummary: null,
            },
          }),
        ]}
      />,
    )
    expect(screen.getByText('No summary provided')).toBeInTheDocument()
  })

  it('renders multiple cycles distinctly with data-feedback-id', () => {
    const feedback: ApprovalFeedback[] = [
      makeFeedback({
        id: 'fb-1',
        body: 'First',
        status: 'resolved',
        createdAt: '2026-01-01T00:00:00.000Z',
        resolution: {
          resolvedAt: '2026-01-01T00:10:00.000Z',
          resolutionSummary: 'Done 1',
        },
      }),
      makeFeedback({
        id: 'fb-2',
        body: 'Second',
        status: 'open',
        createdAt: '2026-01-02T00:00:00.000Z',
      }),
    ]
    render(<FeedbackHistory stage={WorkflowStage.Plan} feedback={feedback} />)
    expect(screen.getByText('Cycle 1')).toBeInTheDocument()
    expect(screen.getByText('Cycle 2')).toBeInTheDocument()
    expect(screen.getByTestId('feedback-fb-1')).toBeInTheDocument()
    expect(screen.getByTestId('feedback-fb-2')).toBeInTheDocument()
    const fb1 = screen.getByTestId('feedback-fb-1') as HTMLElement
    const fb2 = screen.getByTestId('feedback-fb-2') as HTMLElement
    expect(fb1.dataset.feedbackStatus).toBe('resolved')
    expect(fb2.dataset.feedbackStatus).toBe('open')
  })

  it('orders cycles chronologically by createdAt', () => {
    const feedback: ApprovalFeedback[] = [
      makeFeedback({
        id: 'fb-later',
        body: 'Later body',
        createdAt: '2026-01-05T00:00:00.000Z',
      }),
      makeFeedback({
        id: 'fb-earlier',
        body: 'Earlier body',
        createdAt: '2026-01-01T00:00:00.000Z',
      }),
    ]
    render(<FeedbackHistory stage={WorkflowStage.Plan} feedback={feedback} />)
    expect(screen.getByText('Cycle 1').parentElement).toBeInTheDocument()
    expect(screen.getByText('Cycle 2').parentElement).toBeInTheDocument()
    const earlierItem = screen.getByTestId('feedback-fb-earlier') as HTMLElement
    const laterItem = screen.getByTestId('feedback-fb-later') as HTMLElement
    expect(earlierItem.compareDocumentPosition(laterItem) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })

  it('shows feedback cycle count', () => {
    render(
      <FeedbackHistory
        stage={WorkflowStage.Plan}
        feedback={[
          makeFeedback({ id: 'a' }),
          makeFeedback({ id: 'b' }),
          makeFeedback({ id: 'c' }),
        ]}
      />,
    )
    expect(screen.getByText('3 cycles')).toBeInTheDocument()
  })

  it('uses singular "cycle" for one item', () => {
    render(
      <FeedbackHistory
        stage={WorkflowStage.Plan}
        feedback={[makeFeedback({ id: 'a' })]}
      />,
    )
    expect(screen.getByText('1 cycle')).toBeInTheDocument()
  })

  it('shows next approval step between resolved cycles', () => {
    const feedback: ApprovalFeedback[] = [
      makeFeedback({
        id: 'fb-1',
        status: 'resolved',
        body: 'First',
        createdAt: '2026-01-01T00:00:00.000Z',
        resolution: {
          resolvedAt: '2026-01-01T00:10:00.000Z',
          resolutionSummary: 'Done 1',
        },
      }),
      makeFeedback({
        id: 'fb-2',
        status: 'resolved',
        body: 'Second',
        createdAt: '2026-01-02T00:00:00.000Z',
        resolution: {
          resolvedAt: '2026-01-02T00:10:00.000Z',
          resolutionSummary: 'Done 2',
        },
      }),
    ]
    render(<FeedbackHistory stage={WorkflowStage.Plan} feedback={feedback} />)
    expect(screen.getByText('Next approval requested')).toBeInTheDocument()
  })

  it('shows in-progress notification when last feedback is open', () => {
    render(
      <FeedbackHistory
        stage={WorkflowStage.Plan}
        feedback={[makeFeedback({ id: 'fb-open', status: 'open' })]}
      />,
    )
    expect(screen.getByText(/The agent is applying your feedback/)).toBeInTheDocument()
  })

  it('does not show in-progress notification when all feedback is resolved', () => {
    render(
      <FeedbackHistory
        stage={WorkflowStage.Plan}
        feedback={[
          makeFeedback({
            id: 'fb-1',
            status: 'resolved',
            resolution: {
              resolvedAt: '2026-01-01T00:10:00.000Z',
              resolutionSummary: 'Done',
            },
          }),
        ]}
      />,
    )
    expect(screen.queryByText(/The agent is applying your feedback/)).not.toBeInTheDocument()
  })

  it('renders first approval requested timestamp when provided', () => {
    render(
      <FeedbackHistory
        stage={WorkflowStage.Plan}
        feedback={[makeFeedback({ id: 'fb-1' })]}
        approvalRequestedAt="2026-01-01T00:00:00.000Z"
      />,
    )
    expect(screen.getByText(/First approval requested at/)).toBeInTheDocument()
  })

  it('renders latest check rerun info when all feedback resolved and checks provided', () => {
    const checks = [
      {
        checkName: 'review-passed',
        title: 'Review',
        status: 'passed' as const,
        message: null,
        output: null,
        runCount: 1,
        lastRunAt: '2026-01-02T00:00:00.000Z',
        updatedAt: '2026-01-02T00:00:00.000Z',
      },
    ]
    render(
      <FeedbackHistory
        stage={WorkflowStage.Plan}
        feedback={[
          makeFeedback({
            id: 'fb-1',
            status: 'resolved',
            resolution: {
              resolvedAt: '2026-01-01T00:10:00.000Z',
              resolutionSummary: 'Done',
            },
          }),
        ]}
        checks={checks}
      />,
    )
    expect(screen.getByText(/Latest check rerun: review-passed \(passed\)/)).toBeInTheDocument()
  })

  it('reads resolution fields from the nested resolution object', () => {
    const nested = makeFeedback({
      id: 'fb-nested',
      status: 'resolved',
      resolution: {
        resolutionTaskId: 'task-9',
        resolvedAt: '2026-01-01T00:20:00.000Z',
        resolutionSummary: 'Nested summary text',
      },
    })
    expect(nested.resolution?.resolutionTaskId).toBe('task-9')
    expect(nested.resolution?.resolvedAt).toBe('2026-01-01T00:20:00.000Z')
    expect(nested.resolution?.resolutionSummary).toBe('Nested summary text')

    render(
      <FeedbackHistory stage={WorkflowStage.Plan} feedback={[nested]} />,
    )
    expect(screen.getByText('Nested summary text')).toBeInTheDocument()
    expect(screen.getByText('task-9')).toBeInTheDocument()
  })

  it('renders a real WorkflowFeedbackSnapshot JSON payload (server wire shape) end-to-end', () => {
    // This JSON mirrors the exact shape produced by the server's
    // WorkflowFeedbackSnapshot serializer (see IWorkflowGrain.cs:115-123
    // and WorkflowGrain.cs:475-492). Lowercase status, nested resolution
    // object, top-level issueNumber. A regression to flat fields would
    // leave the body/timestamp/task-id unreadable.
    const serverJson = `{
      "id": "fb_wire",
      "issueNumber": 42,
      "workflowRunId": "wr_wire",
      "stage": "plan",
      "status": "resolved",
      "body": "Add error handling around the retry path",
      "createdAt": "2026-01-01T00:00:00.000Z",
      "resolution": {
        "resolutionTaskId": "apply-feedback.7",
        "resolvedAt": "2026-01-01T00:30:00.000Z",
        "resolutionSummary": "Wrapped retry calls in try/catch with structured logging"
      }
    }`
    const parsed = JSON.parse(serverJson) as ApprovalFeedback

    render(
      <FeedbackHistory stage={WorkflowStage.Plan} feedback={[parsed]} />,
    )

    expect(screen.getByText('Add error handling around the retry path')).toBeInTheDocument()
    expect(screen.getByText('Resolved')).toBeInTheDocument()
    expect(screen.getByText('Wrapped retry calls in try/catch with structured logging')).toBeInTheDocument()
    expect(screen.getByText('apply-feedback.7')).toBeInTheDocument()
    expect(screen.getByText('Resolution summary')).toBeInTheDocument()
    expect(screen.getByText('Feedback task applied')).toBeInTheDocument()
  })
})
