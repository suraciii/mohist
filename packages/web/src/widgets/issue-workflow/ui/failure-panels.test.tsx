import { describe, expect, it } from 'vitest'
import { screen } from '@testing-library/react'
import { render } from '../../../../tests/test-utils'
import { IssueHealth, IssueStatus, WorkflowStage, type Issue } from '../../../entities/issue'
import { IntegrateFailurePanel } from './failure-panels'

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    number: 1,
    title: 'Integrate a change',
    body: '',
    status: IssueStatus.InProgress,
    workflowStage: WorkflowStage.Integrate,
    health: IssueHealth.Blocked,
    projectId: 'test-project',
    labels: {},
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
    comments: [],
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
}

describe('IntegrateFailurePanel', () => {
  it('does not classify an archive message as an OpenSpec archive step', () => {
    render(<IntegrateFailurePanel issue={makeIssue({ blockedReason: 'archive change task failed' })} />)

    expect(screen.getByText('Failing step:').parentElement).toHaveTextContent('unknown')
    expect(screen.queryByText('Archive OpenSpec change')).not.toBeInTheDocument()
    expect(screen.queryByText(/Retry the archive step/i)).not.toBeInTheDocument()
  })
})
