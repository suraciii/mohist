import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { ComponentProps } from 'react'
import { fireEvent, render, screen, waitFor, within } from '../../../../tests/test-utils'
import {
  IssueWorkflowProfileEditor as IssueWorkflowProfileEditorView,
  type IssueWorkflowProfileEditorHooks,
} from './IssueWorkflowProfileEditor'
import type { IssueWorkflowProfileYamlResponse } from '../../../entities/issue'

const refetch = vi.fn()

const customData = (): IssueWorkflowProfileYamlResponse => ({
  issueNumber: 1,
  projectId: 'test-project',
  issueKey: 'mohist/test-project#1',
  yaml: 'id: baseline\nstages:\n  - stage: plan\n    tasks: []\n    checks: []\n',
  workflowRunId: null,
  profileId: 'mohist/local',
  updateMode: 'Reference',
  hasCustomTemplate: true,
  templateSource: 'custom',
  variables: {},
  updatedAt: '2024-01-01T00:00:00.000Z',
})

const referenceData = (): IssueWorkflowProfileYamlResponse => ({
  ...customData(),
  yaml: null,
  hasCustomTemplate: false,
  templateSource: 'system',
})

const projectReferenceData = (): IssueWorkflowProfileYamlResponse => ({
  ...referenceData(),
  profileId: 'project/default',
  templateSource: 'project',
})

const state = {
  data: customData() as IssueWorkflowProfileYamlResponse | null,
  isLoading: false,
  error: null as Error | null,
  isPending: false,
  mutate: vi.fn(),
  deleteMutate: vi.fn(),
  deletePending: false,
}

const testHooks = {
  useProfile: () => ({
      data: state.data,
      isLoading: state.isLoading,
      error: state.error,
      refetch,
    }),
  useUpdate: () => ({
      mutate: state.mutate,
      isPending: state.isPending,
    }),
  useDelete: () => ({
      mutate: state.deleteMutate,
      isPending: state.deletePending,
    }),
} as unknown as IssueWorkflowProfileEditorHooks

function IssueWorkflowProfileEditor(
  props: Omit<ComponentProps<typeof IssueWorkflowProfileEditorView>, 'hooks'>,
) {
  return <IssueWorkflowProfileEditorView {...props} hooks={testHooks} />
}

describe('IssueWorkflowProfileEditor', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    state.data = customData()
    state.isLoading = false
    state.error = null
    state.isPending = false
    state.mutate = vi.fn()
    state.deleteMutate = vi.fn()
    state.deletePending = false
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('shows unsaved state after backlog editing', () => {
    render(<IssueWorkflowProfileEditor issueNumber={1} />)

    fireEvent.change(screen.getByRole('textbox'), {
      target: { value: `${state.data!.yaml}# edited\n` },
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
      target: { value: `${state.data!.yaml}# edited\n` },
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
        ...state.data!,
        yaml: normalizedYaml,
        updateMode: 'Custom',
        updatedAt: '2024-01-01T00:00:01.000Z',
      })
    })

    render(<IssueWorkflowProfileEditor issueNumber={1} />)

    const editor = screen.getByRole('textbox') as HTMLTextAreaElement
    fireEvent.change(editor, { target: { value: `${state.data!.yaml}# edited\n` } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => {
      expect(screen.getByText('Saved')).toBeInTheDocument()
    })
    expect(editor.value).toBe(normalizedYaml)
    expect(screen.queryByText('Unsaved changes')).not.toBeInTheDocument()
  })

  it('preserves unsaved draft when query data refreshes with unchanged server yaml', async () => {
    const view = render(<IssueWorkflowProfileEditor issueNumber={1} />)

    const editedYaml = `${state.data!.yaml}# edited\n`
    const editor = screen.getByRole('textbox') as HTMLTextAreaElement
    fireEvent.change(editor, { target: { value: editedYaml } })

    state.data = {
      ...state.data!,
      updatedAt: '2024-01-01T00:00:02.000Z',
    }
    view.rerender(<IssueWorkflowProfileEditor issueNumber={1} />)

    await waitFor(() => {
      expect(editor.value).toBe(editedYaml)
    })
    expect(screen.getByText('Unsaved changes')).toBeInTheDocument()
  })
})

describe('IssueWorkflowProfileEditor (reference mode)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    state.data = referenceData()
    state.isLoading = false
    state.error = null
    state.isPending = false
    state.mutate = vi.fn()
    state.deleteMutate = vi.fn()
    state.deletePending = false
  })

  it('renders the inherited summary with Profile, Mode, Template, and Overrides fields', () => {
    render(<IssueWorkflowProfileEditor issueNumber={1} />)

    const card = screen.getByTestId('workflow-profile-reference')
    expect(card).toBeInTheDocument()

    expect(within(card).getByText('Profile')).toBeInTheDocument()
    expect(within(card).getByText('mohist/local')).toBeInTheDocument()

    expect(within(card).getByText('Mode')).toBeInTheDocument()
    expect(within(card).getByText('Inherited')).toBeInTheDocument()

    expect(within(card).getByText('Template')).toBeInTheDocument()
    expect(within(card).getByText('System default')).toBeInTheDocument()

    expect(within(card).getByText('Overrides')).toBeInTheDocument()
    expect(within(card).getByText('None')).toBeInTheDocument()
  })

  it('renders "Project default" when the inherited source is a project template', () => {
    state.data = projectReferenceData()

    render(<IssueWorkflowProfileEditor issueNumber={1} />)

    const card = screen.getByTestId('workflow-profile-reference')
    expect(within(card).getByText('Project default')).toBeInTheDocument()
    expect(within(card).getByText('project/default')).toBeInTheDocument()
  })

  it('exposes a Customize profile action that opens the editor', () => {
    render(<IssueWorkflowProfileEditor issueNumber={1} />)

    expect(screen.getByTestId('workflow-profile-reference')).toBeInTheDocument()
    expect(screen.queryByTestId('workflow-profile-custom')).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Customize profile' }))

    expect(screen.getByTestId('workflow-profile-custom')).toBeInTheDocument()
    expect(screen.queryByTestId('workflow-profile-reference')).not.toBeInTheDocument()
  })

  it('enables Save after Customize profile and the user types custom YAML', () => {
    render(<IssueWorkflowProfileEditor issueNumber={1} />)

    fireEvent.click(screen.getByRole('button', { name: 'Customize profile' }))

    const editor = screen.getByRole('textbox') as HTMLTextAreaElement
    const customYaml = 'id: custom\nstages:\n  - stage: plan\n    tasks: []\n    checks: []\n'
    fireEvent.change(editor, { target: { value: customYaml } })

    const saveButton = screen.getByRole('button', { name: 'Save' })
    expect(saveButton).toBeEnabled()
  })

  it('invokes the update mutation with the typed YAML after Customize profile and Save', () => {
    render(<IssueWorkflowProfileEditor issueNumber={1} />)

    fireEvent.click(screen.getByRole('button', { name: 'Customize profile' }))

    const customYaml = 'id: custom\nstages:\n  - stage: plan\n    tasks: []\n    checks: []\n'
    fireEvent.change(screen.getByRole('textbox'), { target: { value: customYaml } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    expect(state.mutate).toHaveBeenCalledTimes(1)
    const call = state.mutate.mock.calls[0]
    expect(call[0]).toEqual({ issueNumber: 1, yaml: customYaml })
  })

  it('does not render a textarea, Save button, or the legacy loading placeholder', () => {
    render(<IssueWorkflowProfileEditor issueNumber={1} />)

    expect(screen.queryByRole('textbox')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument()
    expect(screen.queryByText('Loading workflow profile...')).not.toBeInTheDocument()
    expect(screen.queryByText(/Loading workflow profile/)).not.toBeInTheDocument()
  })
})

describe('IssueWorkflowProfileEditor (custom mode)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    state.data = customData()
    state.isLoading = false
    state.error = null
    state.isPending = false
    state.mutate = vi.fn()
    state.deleteMutate = vi.fn()
    state.deletePending = false
  })

  it('renders the populated editor with an issue-owned workflow profile YAML label', () => {
    render(<IssueWorkflowProfileEditor issueNumber={1} />)

    const card = screen.getByTestId('workflow-profile-custom')
    expect(card).toBeInTheDocument()

    const editor = within(card).getByRole('textbox') as HTMLTextAreaElement
    expect(editor.value).toBe(state.data!.yaml)
    expect(editor.value.length).toBeGreaterThan(0)

    expect(
      within(card).getByText(/Editing this issue's own workflow profile YAML/i)
    ).toBeInTheDocument()
  })

  it('exposes a Revert to inherited profile affordance that invokes the delete mutation', () => {
    state.deleteMutate.mockImplementation((vars, options) => {
      options?.onSuccess?.({
        ...state.data!,
        yaml: null,
        hasCustomTemplate: false,
        updateMode: 'Reference',
        templateSource: 'system',
      })
      return vars
    })

    render(<IssueWorkflowProfileEditor issueNumber={1} />)

    const revertButton = screen.getByRole('button', { name: 'Revert to inherited profile' })
    expect(revertButton).toBeInTheDocument()
    fireEvent.click(revertButton)

    expect(state.deleteMutate).toHaveBeenCalledTimes(1)
    const call = state.deleteMutate.mock.calls[0]
    expect(call[0]).toEqual({ issueNumber: 1, })
  })

  it('surfaces revert errors without clearing the editor draft', async () => {
    state.deleteMutate.mockImplementation((vars, options) => {
      options?.onError?.(new Error('revert: server unavailable'))
      return vars
    })

    render(<IssueWorkflowProfileEditor issueNumber={1} />)

    fireEvent.click(screen.getByRole('button', { name: 'Revert to inherited profile' }))

    await waitFor(() => {
      expect(screen.getByText(/server unavailable/)).toBeInTheDocument()
    })
    expect(screen.getByTestId('workflow-profile-custom')).toBeInTheDocument()
    const editor = screen.getByRole('textbox') as HTMLTextAreaElement
    expect(editor.value).toBe(state.data!.yaml)
  })
})

describe('IssueWorkflowProfileEditor (loading state)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    state.data = null
    state.isLoading = true
    state.error = null
    state.isPending = false
    state.mutate = vi.fn()
    state.deleteMutate = vi.fn()
    state.deletePending = false
  })

  it('renders a skeleton and no editor placeholder', () => {
    render(<IssueWorkflowProfileEditor issueNumber={1} />)

    expect(screen.getByTestId('workflow-profile-loading')).toBeInTheDocument()
    expect(screen.queryByTestId('workflow-profile-custom')).not.toBeInTheDocument()
    expect(screen.queryByTestId('workflow-profile-reference')).not.toBeInTheDocument()
    expect(screen.queryByTestId('workflow-profile-error')).not.toBeInTheDocument()

    expect(screen.queryByRole('textbox')).not.toBeInTheDocument()
    expect(screen.queryByText('Loading workflow profile...')).not.toBeInTheDocument()
    expect(screen.queryByText(/Loading workflow profile/)).not.toBeInTheDocument()
  })
})

describe('IssueWorkflowProfileEditor (error state)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    state.data = null
    state.isLoading = false
    state.error = new Error('network down')
    state.isPending = false
    state.mutate = vi.fn()
    state.deleteMutate = vi.fn()
    state.deletePending = false
  })

  it('renders a compact error block with the failure message and a retry control', () => {
    render(<IssueWorkflowProfileEditor issueNumber={1} />)

    const errorBlock = screen.getByTestId('workflow-profile-error')
    expect(errorBlock).toBeInTheDocument()
    expect(within(errorBlock).getByText(/network down/)).toBeInTheDocument()

    const retry = screen.getByRole('button', { name: 'Retry' })
    expect(retry).toBeInTheDocument()
  })

  it('invokes the query refetch when Retry is clicked', () => {
    render(<IssueWorkflowProfileEditor issueNumber={1} />)

    fireEvent.click(screen.getByRole('button', { name: 'Retry' }))
    expect(refetch).toHaveBeenCalledTimes(1)
  })

  it('does not render the editor or the legacy loading placeholder', () => {
    render(<IssueWorkflowProfileEditor issueNumber={1} />)

    expect(screen.queryByTestId('workflow-profile-custom')).not.toBeInTheDocument()
    expect(screen.queryByTestId('workflow-profile-reference')).not.toBeInTheDocument()
    expect(screen.queryByTestId('workflow-profile-loading')).not.toBeInTheDocument()

    expect(screen.queryByRole('textbox')).not.toBeInTheDocument()
    expect(screen.queryByText('Loading workflow profile...')).not.toBeInTheDocument()
    expect(screen.queryByText(/Loading workflow profile/)).not.toBeInTheDocument()
  })
})
