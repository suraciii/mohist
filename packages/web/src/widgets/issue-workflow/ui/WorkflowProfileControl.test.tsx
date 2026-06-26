// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { WorkflowProfileControl } from './WorkflowProfileControl'
import type { Issue } from '../../../entities/issue'

const mockUseWorkflowProfiles = vi.fn<() => { data: { id: string; displayName: string; description: string; isDefault: boolean }[] | undefined }>(() => ({ data: undefined }))
const mockUseEffectiveDefaultWorkflowProfile = vi.fn<() => { effectiveTemplateId: string; source: 'project' | 'system' | 'none'; configuredTemplateId: string | null }>(() => ({
  effectiveTemplateId: 'mohist/local',
  source: 'system',
  configuredTemplateId: null,
}))
const mockUseIssueWorkflowProfileYaml = vi.fn<() => { data: { profileId: string; hasCustomTemplate: boolean } | undefined }>(() => ({ data: undefined }))
const mockUpdateMutation = vi.fn()

vi.mock('../../../entities/settings', () => ({
  useEffectiveDefaultWorkflowProfile: () => mockUseEffectiveDefaultWorkflowProfile(),
  useWorkflowProfiles: () => mockUseWorkflowProfiles(),
}))

vi.mock('../../../entities/issue', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/issue')>()
  return {
    ...actual,
    useIssueWorkflowProfileYaml: () => mockUseIssueWorkflowProfileYaml(),
    useUpdateIssueWorkflowProfile: () => ({
      mutateAsync: mockUpdateMutation,
      isPending: false,
    }),
  }
})

function makeIssue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: 'issue-1',
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
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <WorkflowProfileControl issue={issue} />
    </QueryClientProvider>,
  )
}

describe('WorkflowProfileControl', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockUseIssueWorkflowProfileYaml.mockReturnValue({ data: undefined })
    mockUseEffectiveDefaultWorkflowProfile.mockReturnValue({
      effectiveTemplateId: 'mohist/local',
      source: 'system',
      configuredTemplateId: null,
    })
    mockUpdateMutation.mockResolvedValue({})
  })

  afterEach(() => {
    cleanup()
  })

  it('renders the effective profile id from the issue read model', async () => {
    mockUseWorkflowProfiles.mockReturnValue({
      data: [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
        { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
      ],
    })

    renderControl(makeIssue({ workflowProfileId: 'mohist/github-pr' }))

    await waitFor(() => expect(screen.getByTestId('issue-workflow-profile-value')).toHaveTextContent('mohist/github-pr'))
    const control = screen.getByTestId('issue-workflow-profile-control')
    expect(control.dataset.effectiveProfile).toBe('mohist/github-pr')
    expect(control.dataset.defaultProfile).toBe('mohist/local')
  })

  it('falls back to mohist/local when no issue-level selection and no workflow-profile response', async () => {
    mockUseWorkflowProfiles.mockReturnValue({
      data: [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
      ],
    })

    renderControl(makeIssue({ workflowProfileId: null }))

    await waitFor(() => expect(screen.getByTestId('issue-workflow-profile-value')).toHaveTextContent('mohist/local'))
  })

  it('falls back to the workflow-profile endpoint profile id when the read model omits it', async () => {
    mockUseWorkflowProfiles.mockReturnValue({
      data: [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
        { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
      ],
    })
    mockUseIssueWorkflowProfileYaml.mockReturnValue({
      data: { profileId: 'mohist/github-pr', hasCustomTemplate: false },
    })

    renderControl(makeIssue({ workflowProfileId: undefined }))

    await waitFor(() => expect(screen.getByTestId('issue-workflow-profile-value')).toHaveTextContent('mohist/github-pr'))
  })

  it('sends a PATCH with the new profile id when a backlog issue changes profile', async () => {
    mockUseWorkflowProfiles.mockReturnValue({
      data: [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
        { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
      ],
    })

    renderControl(makeIssue({ workflowProfileId: 'mohist/local', status: 'backlog' as Issue['status'] }))

    const select = await screen.findByTestId('issue-workflow-profile-select') as HTMLSelectElement
    expect(select.disabled).toBe(false)

    fireEvent.change(select, { target: { value: 'mohist/github-pr' } })

    await waitFor(() => expect(mockUpdateMutation).toHaveBeenCalledTimes(1))
    expect(mockUpdateMutation).toHaveBeenCalledWith({ issueNumber: 14, workflowProfileId: 'mohist/github-pr' })
  })

  it('disables the selector and shows a clear reason when the issue has started', async () => {
    mockUseWorkflowProfiles.mockReturnValue({
      data: [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
        { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
      ],
    })

    renderControl(makeIssue({ workflowProfileId: 'mohist/github-pr', status: 'in_progress' as Issue['status'], workflowRunId: 'wr-1' }))

    const select = await screen.findByTestId('issue-workflow-profile-select') as HTMLSelectElement
    expect(select.disabled).toBe(true)
    const reason = screen.getByTestId('issue-workflow-profile-locked-reason')
    expect(reason).toHaveTextContent(/started/i)
    expect(reason).toHaveTextContent(/locked/i)
  })

  it('does not call update when the issue has started and the user cannot trigger a change', async () => {
    mockUseWorkflowProfiles.mockReturnValue({
      data: [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
        { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
      ],
    })

    renderControl(makeIssue({ workflowProfileId: 'mohist/github-pr', status: 'in_progress' as Issue['status'], workflowRunId: 'wr-1' }))

    await screen.findByTestId('issue-workflow-profile-select')
    expect(mockUpdateMutation).not.toHaveBeenCalled()
  })

  it('surfaces the server error text and keeps the prior profile id when the PATCH fails on a started issue', async () => {
    mockUseWorkflowProfiles.mockReturnValue({
      data: [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
        { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
      ],
    })
    mockUpdateMutation.mockRejectedValue(new Error('Cannot change workflow profile: workflow run wr-1 is active'))

    renderControl(makeIssue({ workflowProfileId: 'mohist/local', status: 'backlog' as Issue['status'] }))

    const select = await screen.findByTestId('issue-workflow-profile-select') as HTMLSelectElement
    fireEvent.change(select, { target: { value: 'mohist/github-pr' } })

    await waitFor(() => expect(screen.getByTestId('issue-workflow-profile-error')).toHaveTextContent(/active/))

    expect(screen.getByTestId('issue-workflow-profile-value')).toHaveTextContent('mohist/local')
  })

  it('uses the project-configured default profile id when no issue-level selection exists', async () => {
    mockUseWorkflowProfiles.mockReturnValue({
      data: [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
        { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
      ],
    })
    mockUseEffectiveDefaultWorkflowProfile.mockReturnValue({
      effectiveTemplateId: 'mohist/github-pr',
      source: 'project',
      configuredTemplateId: 'mohist/github-pr',
    })

    renderControl(makeIssue({ workflowProfileId: null }))

    const control = await screen.findByTestId('issue-workflow-profile-control')
    expect(control.dataset.defaultProfile).toBe('mohist/github-pr')
    expect(control.dataset.effectiveProfile).toBe('mohist/github-pr')
    expect(screen.getByTestId('issue-workflow-profile-value')).toHaveTextContent('mohist/github-pr')
    expect(screen.getByTestId('issue-workflow-profile-select')).toHaveValue('mohist/github-pr')
  })

  it('falls back to the system default profile id when the project default is unset', async () => {
    mockUseWorkflowProfiles.mockReturnValue({
      data: [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
        { id: 'mohist/github-pr', displayName: 'PR', description: '', isDefault: false },
      ],
    })
    mockUseEffectiveDefaultWorkflowProfile.mockReturnValue({
      effectiveTemplateId: 'mohist/local',
      source: 'system',
      configuredTemplateId: null,
    })

    renderControl(makeIssue({ workflowProfileId: null }))

    const control = await screen.findByTestId('issue-workflow-profile-control')
    expect(control.dataset.defaultProfile).toBe('mohist/local')
    expect(control.dataset.effectiveProfile).toBe('mohist/local')
    expect(screen.getByTestId('issue-workflow-profile-value')).toHaveTextContent('mohist/local')
    expect(screen.getByTestId('issue-workflow-profile-select')).toHaveValue('mohist/local')
  })

  it('adds the project default to the selector when it is absent from the catalog', async () => {
    mockUseWorkflowProfiles.mockReturnValue({
      data: [
        { id: 'mohist/local', displayName: 'Default', description: '', isDefault: true },
      ],
    })
    mockUseEffectiveDefaultWorkflowProfile.mockReturnValue({
      effectiveTemplateId: 'mohist/github-pr',
      source: 'project',
      configuredTemplateId: 'mohist/github-pr',
    })

    renderControl(makeIssue({ workflowProfileId: null }))

    const select = await screen.findByTestId('issue-workflow-profile-select')
    expect(select).toHaveValue('mohist/github-pr')
    expect(screen.getByRole('option', { name: 'mohist/github-pr' })).toBeInTheDocument()
  })
})
