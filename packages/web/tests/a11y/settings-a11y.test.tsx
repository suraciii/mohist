// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { axe } from 'vitest-axe'
import type { Project } from '../../src/entities/project'
import { ProjectProvider } from '../../src/entities/project'
import { SettingsPage } from '../../src/pages/settings/ui/SettingsPage'

const settingsTabs = ['ai', 'agent', 'repositories', 'workflows', 'templates', 'system', 'preferences'] as const
const focusableSelector = [
  'a[href]',
  'button',
  'input',
  'select',
  'textarea',
  '[tabindex]:not([tabindex="-1"])',
].join(',')

vi.mock('../../src/entities/project', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../src/entities/project')>()
  return {
    ...actual,
    useRepositories: () => ({ data: projects[0].repositories, isLoading: false }),
    useAddRepository: () => ({ mutate: vi.fn(), isPending: false }),
    useRemoveRepository: () => ({ mutate: vi.fn(), isPending: false }),
    useSetDefaultRepository: () => ({ mutate: vi.fn(), isPending: false }),
  }
})

vi.mock('../../src/entities/settings', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../src/entities/settings')>()
  return {
    ...actual,
    useOpencodeRuntime: () => ({ data: { mode: 'local', command: 'opencode', model: null, note: '' }, isLoading: false, error: null }),
    useAvailableModelIds: () => ({ data: ['openai/gpt-4.1', 'anthropic/claude-sonnet-4'], isLoading: false, error: null }),
    useOpencodeModel: () => ({ data: { model: null }, isLoading: false, error: null }),
    useUpdateOpencodeModel: () => ({ mutate: vi.fn(), isPending: false }),
    useStageModels: () => ({ data: { stageModels: null }, isLoading: false, error: null }),
    useSetStageModels: () => ({ mutate: vi.fn(), isPending: false }),
    useAgentRuntime: () => ({ data: agentRuntime, isLoading: false, error: null }),
    useUpdateAgentRuntime: () => ({ mutate: vi.fn(), isPending: false }),
    useLogLevel: () => ({ data: { level: 'INFO' }, isLoading: false, error: null }),
    useSetLogLevel: () => ({ mutate: vi.fn(), isPending: false }),
    useWorkflowProfiles: () => ({ data: workflowProfiles, isLoading: false, error: null }),
    useWorkflowProfile: () => ({ data: workflowProfileDetail, isLoading: false, error: null }),
    useSystemInfo: () => ({ data: systemInfo, isLoading: false, error: null }),
    useSystemUpdateStatus: () => ({ data: { hasJob: false, job: null }, isLoading: false, error: null }),
    useStartSystemUpdate: () => ({ mutate: vi.fn(), isPending: false }),
    useRuntimeConsistency: () => ({ data: runtimeConsistency, isLoading: false, error: null }),
  }
})

vi.mock('../../src/entities/template', () => ({
  useProjectTemplates: () => ({ data: templates, isLoading: false, error: null }),
  useSystemTemplates: () => ({ data: templates, isLoading: false, error: null }),
  useProjectTemplateOverride: () => ({ data: null, isLoading: false, error: null }),
  useUpsertProjectTemplateOverride: () => ({ mutate: vi.fn(), isPending: false }),
  useDeleteProjectTemplateOverride: () => ({ mutate: vi.fn(), isPending: false }),
  usePreviewProjectTemplate: () => ({ mutate: vi.fn(), isPending: false }),
}))

const projects: Project[] = [
  {
    id: 'proj-a11y',
    name: 'a11y-project',
    repositories: [
      { name: 'frontend', gitUrl: 'https://github.com/example/frontend.git', baseBranch: 'main', isDefault: true },
      { name: 'backend', gitUrl: 'https://github.com/example/backend.git', baseBranch: 'develop', isDefault: false },
    ],
    createdAt: '2026-06-01T00:00:00Z',
    updatedAt: '2026-06-01T00:00:00Z',
  },
]

const agentRuntime = {
  timeout: 600000,
  taskTimeout: 900000,
  stageTimeout: 1800000,
  maxConcurrent: 2,
  maxGracePeriods: 1,
  pollInterval: 5000,
}

const workflowProfiles = [
  { id: 'mohist/local', displayName: 'Default', description: 'Default workflow', isDefault: true },
]

const workflowProfileDetail = {
  id: 'mohist/local',
  displayName: 'Default',
  description: 'Default workflow',
  isDefault: true,
  yaml: 'stages:\n  build: {}\n',
  stages: [{ stage: 'build', requiresApproval: false, tasks: ['Task'], checks: ['Check'] }],
}

const systemInfo = {
  running: { version: '0.1.0', gitHash: 'abcdef1', startedAt: '2026-06-01T00:00:00Z' },
  source: { path: '/repo', branch: 'master', head: 'abcdef1', dirty: false },
  install: { mode: 'local-source', serviceManager: null, serverUnit: null, runnerUnit: null, reason: null },
  update: { status: 'up-to-date', available: false, reason: null },
  services: { server: 'active', runner: 'active' },
  paths: { db: '/tmp/mohist.db', config: '/tmp/config.json', opencode: '/tmp/opencode', logs: '/tmp/logs' },
}

const runtimeConsistency = {
  status: 'consistent',
  reason: null,
  components: [{ name: 'server', status: 'ok', reason: null }],
}

const templates = [
  {
    key: 'plan',
    displayName: 'Plan',
    description: 'Planning template',
    tags: ['workflow'],
    stage: 'plan',
    body: 'Plan the issue',
    source: 'system',
  },
]

function renderSettingsTab(tab: typeof settingsTabs[number]) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-a11y" initialProjects={projects}>
        <MemoryRouter initialEntries={[`/settings/${tab}`]}>
          <Routes>
            <Route path="/settings/:section" element={<SettingsPage />} />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('Settings accessibility structural baseline', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it.each(settingsTabs)('runs structural axe rules for the %s tab', async (tab) => {
    const { container } = renderSettingsTab(tab)

    const results = await axe(container, {
      runOnly: {
        type: 'rule',
        values: [
          'aria-allowed-attr',
          'aria-allowed-role',
          'aria-command-name',
          'aria-dialog-name',
          'aria-hidden-body',
          'aria-hidden-focus',
          'aria-input-field-name',
          'aria-required-attr',
          'aria-required-children',
          'aria-required-parent',
          'aria-roles',
          'aria-toggle-field-name',
          'aria-valid-attr-value',
          'aria-valid-attr',
          'button-name',
          'heading-order',
          'label',
          'tabindex',
        ],
      },
    })

    expect(results.violations).toEqual([])
  })

  it.each(settingsTabs)('renders one monotone Settings heading hierarchy for the %s tab', (tab) => {
    const { container } = renderSettingsTab(tab)

    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1)
    expect(screen.getByRole('heading', { level: 1, name: 'Settings' })).toBeInTheDocument()

    const levels = Array.from(container.querySelectorAll('h1,h2,h3,h4,h5,h6')).map((heading) => Number(heading.tagName.slice(1)))
    expect(levels[0]).toBe(1)

    for (let index = 1; index < levels.length; index += 1) {
      expect(levels[index]).toBeLessThanOrEqual(levels[index - 1] + 1)
    }
  })

  it('associates AiSettingsSection ModelSelect controls with visible labels', async () => {
    const user = userEvent.setup()
    renderSettingsTab('ai')

    expect(screen.getByRole('button', { name: 'Default Coder Agent Model' })).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: /Stage Model Overrides/i }))

    for (const stage of ['plan', 'build', 'check', 'integrate']) {
      expect(screen.getByRole('button', { name: stage })).toBeInTheDocument()
    }
  })

  it.each(['ai', 'repositories'] as const)('tabs through the %s tab interactive elements in DOM order', async (tab) => {
    const user = userEvent.setup()
    const { container } = renderSettingsTab(tab)
    const interactiveElements = Array.from(container.querySelectorAll<HTMLElement>(focusableSelector))
      .filter((element) => element.getAttribute('tabindex') !== '-1' && !element.hasAttribute('disabled') && element.getAttribute('aria-hidden') !== 'true')

    expect(interactiveElements.length).toBeGreaterThan(0)

    await user.tab()
    expect(interactiveElements[0]).toHaveFocus()

    for (const element of interactiveElements.slice(1)) {
      await user.tab()
      expect(element).toHaveFocus()
    }

    for (const element of [...interactiveElements].reverse().slice(1)) {
      await user.tab({ shift: true })
      expect(element).toHaveFocus()
    }
  })
})
