// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from './test-utils'
import { IssueWorkflowProfileEditor } from '../src/widgets/issue-workflow/ui/IssueWorkflowProfileEditor'

const state = {
  data: {
    issueNumber: 1,
    projectId: 'test-project',
    yaml: 'id: baseline\nstages:\n  - stage: plan\n    tasks: []\n    checks: []\n',
    workflowRunId: null as string | null,
    profileId: 'mohist/default',
    updateMode: 'Reference',
    updatedAt: '2024-01-01T00:00:00.000Z',
  },
  isLoading: false,
  error: null as Error | null,
  isPending: false,
  mutate: vi.fn(),
}

vi.mock('../src/entities/issue', () => {
  return {
    useIssueWorkflowProfileYaml: () => ({
      data: state.data,
      isLoading: state.isLoading,
      error: state.error,
    }),
    useUpdateIssueWorkflowProfileYaml: () => ({
      mutate: state.mutate,
      isPending: state.isPending,
    }),
  }
})

describe('IssueWorkflowProfileEditor', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    state.data = {
      issueNumber: 1,
      projectId: 'test-project',
      yaml: 'id: baseline\nstages:\n  - stage: plan\n    tasks: []\n    checks: []\n',
      workflowRunId: null,
      profileId: 'mohist/default',
      updateMode: 'Reference',
      updatedAt: '2024-01-01T00:00:00.000Z',
    }
    state.isLoading = false
    state.error = null
    state.isPending = false
    state.mutate = vi.fn()
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('shows unsaved state after backlog editing', () => {
    render(<IssueWorkflowProfileEditor issueNumber={1} />)

    fireEvent.change(screen.getByRole('textbox'), {
      target: { value: `${state.data.yaml}# edited\n` },
    })

    expect(screen.getByText('Unsaved changes')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled()
  })

  it('shows pending save state until save resolves', async () => {
    state.mutate.mockImplementation(() => {
      state.isPending = true
    })

    const view = render(<IssueWorkflowProfileEditor issueNumber={1} />)
    fireEvent.change(screen.getByRole('textbox'), {
      target: { value: `${state.data.yaml}# edited\n` },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))
    view.rerender(<IssueWorkflowProfileEditor issueNumber={1} />)

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Saving...' })).toBeDisabled()
    })
  })

  it('shows inline validation errors without clearing draft content', async () => {
    state.mutate.mockImplementation((_, options) => {
      options.onError(new Error('yaml_syntax: bad yaml'))
    })

    render(<IssueWorkflowProfileEditor issueNumber={1} />)

    const nextDraft = 'id: broken\nstages: [\n'
    const editor = screen.getByRole('textbox') as HTMLTextAreaElement
    fireEvent.change(editor, { target: { value: nextDraft } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => {
      expect(screen.getByText('yaml_syntax: bad yaml')).toBeInTheDocument()
    })
    expect(editor.value).toBe(nextDraft)
    expect(screen.getByText('Unsaved changes')).toBeInTheDocument()
  })

  it('resets draft and dirty state to normalized yaml after successful save', async () => {
    const normalizedYaml = 'id: normalized\nstages:\n  - stage: plan\n    tasks: []\n    checks: []\n'
    state.mutate.mockImplementation((_, options) => {
      options.onSuccess({
        ...state.data,
        yaml: normalizedYaml,
        updateMode: 'Custom',
        updatedAt: '2024-01-01T00:00:01.000Z',
      })
    })

    render(<IssueWorkflowProfileEditor issueNumber={1} />)

    const editor = screen.getByRole('textbox') as HTMLTextAreaElement
    fireEvent.change(editor, { target: { value: `${state.data.yaml}# edited\n` } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => {
      expect(screen.getByText('Saved')).toBeInTheDocument()
    })
    expect(editor.value).toBe(normalizedYaml)
    expect(screen.queryByText('Unsaved changes')).not.toBeInTheDocument()
  })

  it('preserves unsaved draft when query data refreshes with unchanged server yaml', async () => {
    const view = render(<IssueWorkflowProfileEditor issueNumber={1} />)

    const editedYaml = `${state.data.yaml}# edited\n`
    const editor = screen.getByRole('textbox') as HTMLTextAreaElement
    fireEvent.change(editor, { target: { value: editedYaml } })

    state.data = {
      ...state.data,
      updatedAt: '2024-01-01T00:00:02.000Z',
    }
    view.rerender(<IssueWorkflowProfileEditor issueNumber={1} />)

    await waitFor(() => {
      expect(editor.value).toBe(editedYaml)
    })
    expect(screen.getByText('Unsaved changes')).toBeInTheDocument()
  })
})
