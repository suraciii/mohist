import { describe, expect, it } from 'vitest'
import { screen } from '@testing-library/react'
import { render } from '../../../../tests/test-utils'
import { WorkflowStage } from '../../../entities/issue'
import { InlineApprovalControls } from './InlineApproval'

describe('InlineApprovalControls', () => {
  it('describes plan approval in terms of plan artifacts', () => {
    render(<InlineApprovalControls issueNumber={1} stage={WorkflowStage.Plan} />)

    expect(screen.getByText('Review the plan artifacts and approve to continue the workflow.')).toBeInTheDocument()
    expect(screen.queryByText(/design proposal/i)).not.toBeInTheDocument()
  })

  it('describes check approval in terms of review evidence', () => {
    render(<InlineApprovalControls issueNumber={1} stage={WorkflowStage.Check} />)

    expect(screen.getByText('Review the review evidence and approve to continue the workflow.')).toBeInTheDocument()
    expect(screen.queryByText(/check results/i)).not.toBeInTheDocument()
  })
})
