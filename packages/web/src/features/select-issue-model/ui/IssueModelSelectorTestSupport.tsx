import { afterEach, beforeEach, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../../../entities/project'
import {
  IssueModelSelector,
  type IssueModelSelectorDependencies,
} from './IssueModelSelector'

export const mocks = {
  useAvailableModelIds: vi.fn(),
  useOpencodeModel: vi.fn(),
  useModelVariants: vi.fn(() => ({})),
  getIssueWorkflowVariables: vi.fn(),
  patchIssueWorkflowDefinitionVar: vi.fn(),
  patchIssueWorkflowStageDefinitionVar: vi.fn(),
}

const dependencies = {
  useAvailableModelIds: () => mocks.useAvailableModelIds(),
  useOpencodeModel: () => mocks.useOpencodeModel(),
  useModelVariants: () => mocks.useModelVariants(),
  getIssueWorkflowVariables: mocks.getIssueWorkflowVariables,
  patchIssueWorkflowDefinitionVar: mocks.patchIssueWorkflowDefinitionVar,
  patchIssueWorkflowStageDefinitionVar: mocks.patchIssueWorkflowStageDefinitionVar,
} as unknown as IssueModelSelectorDependencies

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

beforeEach(() => {
  window.localStorage.clear()
  mocks.useOpencodeModel.mockReturnValue({ data: { model: null, variant: null } })
  mocks.getIssueWorkflowVariables.mockResolvedValue({ vars: {}, stages: {} })
  mocks.patchIssueWorkflowDefinitionVar.mockResolvedValue({ vars: { agent: {} }, stages: {} })
  mocks.patchIssueWorkflowStageDefinitionVar.mockResolvedValue({ vars: {}, stages: {} })
})

export function renderSelector(props: { currentModel?: string | null; currentStageModels?: Record<string, string> | null } = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj_test" initialProjects={[{ id: 'proj_test', name: 'Test', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', repositories: [] }]}>
        <IssueModelSelector
          issueNumber={42}
          currentModel={props.currentModel ?? null}
          currentStageModels={props.currentStageModels ?? null}
          dependencies={dependencies}
        />
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

export function openAdvanced() {
  fireEvent.click(screen.getByRole('button', { name: /Per-stage overrides/i }))
}
