import { vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../../../entities/project'
import type { AgentRuntime } from '../../../entities/settings'
import {
  IssueModelSelector,
  type IssueModelSelectorDependencies,
} from './IssueModelSelector'

export const mocks = {
  useAvailableModelIds: vi.fn(),
  useOpencodeModel: vi.fn(),
  useModelVariants: vi.fn((_runtime?: AgentRuntime | string) => ({})),
  useWorkflowProfiles: vi.fn(),
  useEffectiveDefaultWorkflowProfile: vi.fn(),
  getIssueWorkflowVariables: vi.fn(),
  patchIssueWorkflowDefinitionVar: vi.fn(),
  patchIssueWorkflowStageDefinitionVar: vi.fn(),
}

const dependencies = {
  useAvailableModelIds: (runtime?: AgentRuntime | string) => mocks.useAvailableModelIds(runtime),
  useOpencodeModel: () => mocks.useOpencodeModel(),
  useModelVariants: (runtime?: AgentRuntime | string) => mocks.useModelVariants(runtime),
  useWorkflowProfiles: () => mocks.useWorkflowProfiles(),
  useEffectiveDefaultWorkflowProfile: () => mocks.useEffectiveDefaultWorkflowProfile(),
  getIssueWorkflowVariables: mocks.getIssueWorkflowVariables,
  patchIssueWorkflowDefinitionVar: mocks.patchIssueWorkflowDefinitionVar,
  patchIssueWorkflowStageDefinitionVar: mocks.patchIssueWorkflowStageDefinitionVar,
} as unknown as IssueModelSelectorDependencies

export function resetIssueModelSelectorTestState() {
  vi.clearAllMocks()
  window.localStorage.clear()
  mocks.useOpencodeModel.mockReturnValue({ data: { model: null, variant: null } })
  mocks.useWorkflowProfiles.mockReturnValue({ data: [{ id: 'mohist/local', displayName: 'Default', description: '', isDefault: true, agentRuntime: 'opencode' }] })
  mocks.useEffectiveDefaultWorkflowProfile.mockReturnValue({ effectiveTemplateId: 'mohist/local' })
  mocks.getIssueWorkflowVariables.mockResolvedValue({ vars: {}, stages: {} })
  mocks.patchIssueWorkflowDefinitionVar.mockResolvedValue({ vars: { agent: {} }, stages: {} })
  mocks.patchIssueWorkflowStageDefinitionVar.mockResolvedValue({ vars: {}, stages: {} })
}

export function renderSelector(props: { currentModel?: string | null; currentStageModels?: Record<string, string> | null; workflowProfileId?: string | null } = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj_test" initialProjects={[{ id: 'proj_test', name: 'Test', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', repositories: [] }]}>
          <IssueModelSelector
          issueNumber={42}
          workflowProfileId={props.workflowProfileId}
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
