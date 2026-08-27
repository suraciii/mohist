import { render, screen } from '@testing-library/react'
import { IssueHealth, IssueStatus, type Issue } from '../../../entities/issue'
import { NoWorkflowPanel } from './NoWorkflowPanel'

const issue: Issue = {
  number: 42,
  title: 'External delivery',
  status: IssueStatus.InProgress,
  health: IssueHealth.Active,
  projectId: 'project',
  labels: {},
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  isDraft: false,
  canStart: false,
  blocker: null,
  noWorkflow: true,
}

describe('NoWorkflowPanel', () => {
  it('explains lifecycle without showing workflow execution', () => {
    render(<NoWorkflowPanel issue={issue} />)

    expect(screen.getByTestId('no-workflow-panel')).toHaveTextContent('No workflow')
    expect(screen.getByText(/not run by a Mohist Workflow/)).toBeInTheDocument()
    expect(screen.getByText(/Mark it done/)).toBeInTheDocument()
  })
})
