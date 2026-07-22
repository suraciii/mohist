import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { WorkflowProfileControl, type WorkflowProfileControlDataHook } from './WorkflowProfileControl'
import type { Issue } from '../../../entities/issue'

interface WorkflowProfileFixture {
  id: string
  displayName: string
  description: string
  isDefault: boolean
}

let workflowProfiles: WorkflowProfileFixture[] = []
let projectDefaultTemplateId: string | null = null
let issueWorkflowProfile: { profileId: string; hasCustomTemplate: boolean } | null = null
let updateError: string | null = null
let updateRequests: Array<{ issueNumber: number; workflowProfileId: string | null }> = []

const dataHook: WorkflowProfileControlDataHook = () => ({
  workflowProfiles,
  workflowProfileYaml: issueWorkflowProfile,
  defaultProfileId: projectDefaultTemplateId
    ?? workflowProfiles.find((profile) => profile.isDefault)?.id
    ?? 'mohist/local',
  updateMutation: {
    mutateAsync: async (variables) => {
      updateRequests.push(variables)
      if (updateError) throw new Error(updateError)
      return makeIssue({
        number: variables.issueNumber,
        workflowProfileId: variables.workflowProfileId,
      })
    },
    isPending: false,
  },
})

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    number: 14,
    title: 'Test Issue',
    body: '',
    status: 'backlog' as Issue['status'],
    health: 'active' as Issue['health'],
    projectId: 'proj-1',
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  } as Issue
}

function renderControl(issue: Issue) {
  return render(<WorkflowProfileControl issue={issue} dataHook={dataHook} />)
}

describe('WorkflowProfileControl', () => {
  beforeEach(() => {
    workflowProfiles = []
    projectDefaultTemplateId = null
    issueWorkflowProfile = null
    updateError = null
    updateRequests = []
  })

  afterEach(() => {
    cleanup()
  })

  it('renders the effective profile id from the issue read model', async () => {
    workflowProfiles = [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
        { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
    ]

    renderControl(makeIssue({ workflowProfileId: 'mohist/github-pr' }))

    await waitFor(() => {
      const control = screen.getByTestId('issue-workflow-profile-control')
      expect(control.dataset.effectiveProfile).toBe('mohist/github-pr')
      expect(control.dataset.defaultProfile).toBe('mohist/local')
    })
  })

  it('falls back to mohist/local when no issue-level selection and no workflow-profile response', async () => {
    workflowProfiles = [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
    ]

    renderControl(makeIssue({ workflowProfileId: null }))

    await waitFor(() => expect(screen.getByTestId('issue-workflow-profile-value')).toHaveTextContent('mohist/local'))
  })

  it('falls back to the workflow-profile endpoint profile id when the read model omits it', async () => {
    workflowProfiles = [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
        { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
    ]
    issueWorkflowProfile = { profileId: 'mohist/github-pr', hasCustomTemplate: false }

    renderControl(makeIssue({ workflowProfileId: undefined }))

    await waitFor(() => expect(screen.getByTestId('issue-workflow-profile-value')).toHaveTextContent('mohist/github-pr'))
  })

  it('sends a PATCH with the new profile id when a backlog issue changes profile', async () => {
    workflowProfiles = [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
        { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
    ]

    renderControl(makeIssue({ workflowProfileId: 'mohist/local', status: 'backlog' as Issue['status'] }))

    const user = userEvent.setup()
    const select = screen.getByTestId('issue-workflow-profile-select')
    expect(select.tagName).not.toBe('SELECT')
    expect(select).not.toBeDisabled()

    await user.click(select)
    await user.click(await screen.findByRole('option', { name: 'PR' }))

    await waitFor(() => expect(updateRequests).toHaveLength(1))
    expect(updateRequests[0]).toEqual({ issueNumber: 14, workflowProfileId: 'mohist/github-pr' })
  })

  it('disables the selector and shows a clear reason when the issue has started', async () => {
    workflowProfiles = [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
        { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
    ]

    renderControl(makeIssue({ workflowProfileId: 'mohist/github-pr', status: 'in_progress' as Issue['status'], workflowRunId: 'wr-1' }))

    const select = await screen.findByTestId('issue-workflow-profile-select')
    expect(select.tagName).not.toBe('SELECT')
    expect(select).toBeDisabled()
    const reason = screen.getByTestId('issue-workflow-profile-locked-reason')
    expect(reason).toHaveTextContent(/started/i)
    expect(reason).toHaveTextContent(/locked/i)
  })

  it('does not call update when the issue has started and the user cannot trigger a change', async () => {
    workflowProfiles = [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
        { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
    ]

    renderControl(makeIssue({ workflowProfileId: 'mohist/github-pr', status: 'in_progress' as Issue['status'], workflowRunId: 'wr-1' }))

    await screen.findByTestId('issue-workflow-profile-select')
    expect(updateRequests).toHaveLength(0)
  })

  it('surfaces the server error text and keeps the prior profile id when the PATCH fails on a started issue', async () => {
    workflowProfiles = [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
        { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
    ]
    updateError = 'Cannot change workflow profile: workflow run wr-1 is active'

    renderControl(makeIssue({ workflowProfileId: 'mohist/local', status: 'backlog' as Issue['status'] }))

    const user = userEvent.setup()
    const select = await screen.findByTestId('issue-workflow-profile-select')
    await user.click(select)
    await user.click(await screen.findByRole('option', { name: 'PR' }))

    await waitFor(() => expect(screen.getByTestId('issue-workflow-profile-error')).toHaveTextContent(/active/))

    expect(screen.getByTestId('issue-workflow-profile-value')).toHaveTextContent('mohist/local')
  })

  it('uses the project-configured default profile id when no issue-level selection exists', async () => {
    workflowProfiles = [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
        { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
    ]
    projectDefaultTemplateId = 'mohist/github-pr'

    renderControl(makeIssue({ workflowProfileId: null }))

    await waitFor(() => {
      const control = screen.getByTestId('issue-workflow-profile-control')
      expect(control.dataset.defaultProfile).toBe('mohist/github-pr')
      expect(control.dataset.effectiveProfile).toBe('mohist/github-pr')
      expect(screen.getByTestId('issue-workflow-profile-value')).toHaveTextContent('mohist/github-pr')
      expect(screen.getByTestId('issue-workflow-profile-select')).toHaveTextContent('PR')
    })
  })

  it('falls back to the system default profile id when the project default is unset', async () => {
    workflowProfiles = [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
        { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
    ]

    renderControl(makeIssue({ workflowProfileId: null }))

    await waitFor(() => {
      const control = screen.getByTestId('issue-workflow-profile-control')
      expect(control.dataset.defaultProfile).toBe('mohist/local')
      expect(control.dataset.effectiveProfile).toBe('mohist/local')
      expect(screen.getByTestId('issue-workflow-profile-value')).toHaveTextContent('mohist/local')
      expect(screen.getByTestId('issue-workflow-profile-select')).toHaveTextContent('Default')
    })
  })

  it('adds the project default to the selector when it is absent from the catalog', async () => {
    workflowProfiles = [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
    ]
    projectDefaultTemplateId = 'mohist/github-pr'

    renderControl(makeIssue({ workflowProfileId: null }))

    await waitFor(() => {
      expect(screen.getByTestId('issue-workflow-profile-select')).toHaveTextContent('mohist/github-pr')
    })
    await userEvent.setup().click(screen.getByTestId('issue-workflow-profile-select'))
    expect(await screen.findByRole('option', { name: 'mohist/github-pr' })).toBeInTheDocument()
  })
})
