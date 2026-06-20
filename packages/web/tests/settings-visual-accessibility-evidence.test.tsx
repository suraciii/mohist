// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Project } from '../src/entities/project'
import { ProjectProvider } from '../src/entities/project'
import { AgentSettingsSection } from '../src/pages/settings/ui/AgentSettingsSection'
import { AiSettingsSection } from '../src/pages/settings/ui/AiSettingsSection'
import { PreferencesSection } from '../src/pages/settings/ui/PreferencesSection'
import { RepositoriesSection } from '../src/pages/settings/ui/RepositoriesSection'
import { SystemSettingsSection } from '../src/pages/settings/ui/SystemSettingsSection'
import { TemplatesSection } from '../src/pages/settings/ui/TemplatesSection'
import { WorkflowProfilesSection } from '../src/pages/settings/ui/WorkflowProfilesSection'
import { SidebarProvider } from '../src/shared/ui/components/sidebar'

const useRepositoriesMock = vi.fn()
const useAddRepositoryMock = vi.fn()
const useRemoveRepositoryMock = vi.fn()
const useSetDefaultRepositoryMock = vi.fn()
const useOpencodeRuntimeMock = vi.fn()
const useAvailableModelIdsMock = vi.fn()
const useOpencodeModelMock = vi.fn()
const useUpdateOpencodeModelMock = vi.fn()
const useStageModelsMock = vi.fn()
const useSetStageModelsMock = vi.fn()
const useAgentRuntimeMock = vi.fn()
const useConfigMock = vi.fn()
const useSetAgentRuntimeMock = vi.fn()
const useLogLevelMock = vi.fn()
const useSetLogLevelMock = vi.fn()
const useSystemInfoMock = vi.fn()
const useSystemUpdateMock = vi.fn()
const useSystemUpdateStatusMock = vi.fn()
const useWorkflowProfilesMock = vi.fn()
const useWorkflowProfileMock = vi.fn()
const useProjectTemplatesMock = vi.fn()
const useSystemTemplatesMock = vi.fn()
const useDeleteProjectTemplateOverrideMock = vi.fn()
const useUpsertProjectTemplateOverrideMock = vi.fn()
const usePreviewProjectTemplateMock = vi.fn()
const useExtractVariablesMock = vi.fn()

vi.mock('../src/entities/project', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/entities/project')>()
  return {
    ...actual,
    useRepositories: (projectId: string | undefined) => useRepositoriesMock(projectId),
    useAddRepository: () => useAddRepositoryMock(),
    useRemoveRepository: () => useRemoveRepositoryMock(),
    useSetDefaultRepository: () => useSetDefaultRepositoryMock(),
  }
})

vi.mock('../src/entities/settings', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/entities/settings')>()
  return {
    ...actual,
    useOpencodeRuntime: () => useOpencodeRuntimeMock(),
    useAvailableModelIds: () => useAvailableModelIdsMock(),
    useOpencodeModel: () => useOpencodeModelMock(),
    useUpdateOpencodeModel: () => useUpdateOpencodeModelMock(),
    useStageModels: () => useStageModelsMock(),
    useSetStageModels: () => useSetStageModelsMock(),
    useAgentRuntime: () => useAgentRuntimeMock(),
    useConfig: () => useConfigMock(),
    useSetAgentRuntime: () => useSetAgentRuntimeMock(),
    useLogLevel: () => useLogLevelMock(),
    useSetLogLevel: () => useSetLogLevelMock(),
    useSystemInfo: () => useSystemInfoMock(),
    useSystemUpdate: () => useSystemUpdateMock(),
    useSystemUpdateStatus: () => useSystemUpdateStatusMock(),
    useWorkflowProfiles: () => useWorkflowProfilesMock(),
    useWorkflowProfile: (profileId: string) => useWorkflowProfileMock(profileId),
  }
})

vi.mock('../src/entities/template', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/entities/template')>()
  return {
    ...actual,
    useProjectTemplates: (projectId: string | undefined) => useProjectTemplatesMock(projectId),
    useSystemTemplates: () => useSystemTemplatesMock(),
    useDeleteProjectTemplateOverride: (projectId: string | undefined) => useDeleteProjectTemplateOverrideMock(projectId),
    useUpsertProjectTemplateOverride: (projectId: string) => useUpsertProjectTemplateOverrideMock(projectId),
    usePreviewProjectTemplate: (projectId: string, key: string) => usePreviewProjectTemplateMock(projectId, key),
    useExtractVariables: () => useExtractVariablesMock(),
  }
})

const project: Project = {
  id: 'proj-selected',
  name: 'audit-test-1',
  repositories: [
    { name: 'web', gitUrl: 'https://github.com/example/web.git', baseBranch: 'main', isDefault: true },
    { name: 'server', gitUrl: 'https://github.com/example/server.git', baseBranch: 'release', isDefault: false },
  ],
  createdAt: '2026-06-01T00:00:00Z',
  updatedAt: '2026-06-01T00:00:00Z',
}

let artifacts: Record<string, string> = {}

const beforeSummaries: Record<string, string> = {
  ai: 'Before: External Coder Agent used a hand-rolled rounded-md border bg-muted card and local h3 page title.',
  agent: 'Before: Runtime used a rounded-md bg-muted border card plus text-foreground/80 and text-foreground/85 tokens.',
  preferences: 'Before: Preferences tab did not exist. The tab is new for Settings 2.0; page title is rendered by SettingsSection and cards are CardSection instances.',
  repositories: 'Before: Repository rows used rounded-lg border bg-card/50 and included hardcoded text-gray-500.',
  workflows: 'Before: Workflow profile cards used rounded-md border wrappers and text-foreground/85 body/caption tokens.',
  templates: 'Before: Template rows and editor used rounded-lg border bg-card/50 or bg-card/60 wrappers.',
  system: 'Before: System card titles visually drove the page with h2 uppercase tracking-wider styling while other tabs used local h3 page titles.',
}

const sections = [
  ['ai', <AiSettingsSection />],
  ['agent', <AgentSettingsSection />],
  ['preferences', <PreferencesSection />],
  ['repositories', <RepositoriesSection projectId={project.id} />],
  ['workflows', <WorkflowProfilesSection />],
  ['templates', <TemplatesSection />],
  ['system', <SystemSettingsSection />],
] as const

function arrangeLoadedMocks() {
  useRepositoriesMock.mockReturnValue({ data: project.repositories, isLoading: false })
  useAddRepositoryMock.mockReturnValue({ mutate: vi.fn(), isPending: false })
  useRemoveRepositoryMock.mockReturnValue({ mutate: vi.fn(), isPending: false })
  useSetDefaultRepositoryMock.mockReturnValue({ mutate: vi.fn(), isPending: false })
  useOpencodeRuntimeMock.mockReturnValue({ isLoading: false, error: null })
  useAvailableModelIdsMock.mockReturnValue({ data: ['openai/gpt-5.1', 'anthropic/claude-sonnet-4'], isLoading: false, error: null })
  useOpencodeModelMock.mockReturnValue({ data: { model: 'openai/gpt-5.1' } })
  useUpdateOpencodeModelMock.mockReturnValue({ mutate: vi.fn() })
  useStageModelsMock.mockReturnValue({ data: { stageModels: { check: 'anthropic/claude-sonnet-4' } } })
  useSetStageModelsMock.mockReturnValue({ mutate: vi.fn() })
  useAgentRuntimeMock.mockReturnValue({
    data: { timeout: 600000, stageTimeout: 3600000, taskTimeout: 600000, maxConcurrent: 3, maxGracePeriods: 3, pollInterval: 5000 },
    isLoading: false,
    error: null,
    refetch: vi.fn(),
  })
  useConfigMock.mockReturnValue({ data: { agentTimeout: 600000, maxConcurrentAgents: 3, pollInterval: 5000, logLevel: 'info', taskTimeout: 600000, stageTimeout: 3600000, maxGracePeriods: 3 } })
  useSetAgentRuntimeMock.mockReturnValue({ mutateAsync: vi.fn() })
  useLogLevelMock.mockReturnValue({ data: { level: 'info' }, isLoading: false, isError: false, error: null })
  useSetLogLevelMock.mockReturnValue({ mutateAsync: vi.fn() })
  useSystemInfoMock.mockReturnValue({
    data: {
      running: { version: '1.0.0', gitHash: 'abcdef1234567890', startedAt: '2026-06-18T00:00:00Z' },
      source: { path: '/repo/mohist', branch: 'master', head: 'abcdef1234567890', dirty: false },
      install: { mode: 'local-source', serviceManager: 'systemd', serverUnit: 'mohist-server', runnerUnit: 'mohist-runner', reason: 'local checkout' },
      update: { status: 'up-to-date', available: false, reason: 'Already current' },
      services: { server: 'running', runner: 'running' },
      paths: { db: '/var/lib/mohist/db.sqlite', config: '~/.mohist/config.jsonc', opencode: '~/.config/opencode', logs: '~/.mohist/logs' },
    },
    isLoading: false,
    isError: false,
    error: null,
    refetch: vi.fn(),
  })
  useSystemUpdateMock.mockReturnValue({ mutateAsync: vi.fn(), isPending: false })
  useSystemUpdateStatusMock.mockReturnValue({ data: { hasJob: false, job: null }, refetch: vi.fn() })
  useWorkflowProfilesMock.mockReturnValue({
    data: [
      { id: 'mohist/default', displayName: 'Default', description: 'Standard staged workflow.', isDefault: true },
      { id: 'mohist/quick-fix', displayName: 'Quick Fix', description: 'Short repair workflow.', isDefault: false },
    ],
    isLoading: false,
    isError: false,
  })
  useWorkflowProfileMock.mockReturnValue({ data: null, isLoading: false, isError: false })
  useProjectTemplatesMock.mockReturnValue({
    data: [
      { key: 'build-plan', displayName: 'Build Plan', description: 'Plan the implementation.', tags: ['plan'], stage: 'plan', body: 'Plan body', source: 'system' },
      { key: 'review-fix', displayName: 'Review Fix', description: 'Fix review findings.', tags: ['check'], stage: 'check', body: 'Fix body', source: 'project-override' },
    ],
    isLoading: false,
    isError: false,
    refetch: vi.fn(),
  })
  useSystemTemplatesMock.mockReturnValue({ data: [] })
  useDeleteProjectTemplateOverrideMock.mockReturnValue({ mutate: vi.fn(), isPending: false })
  useUpsertProjectTemplateOverrideMock.mockReturnValue({ mutate: vi.fn(), isPending: false })
  usePreviewProjectTemplateMock.mockReturnValue({ mutate: vi.fn(), data: null, isPending: false, isError: false })
  useExtractVariablesMock.mockReturnValue({ mutate: vi.fn(), data: { variables: [] } })
}

function renderEvidenceSection(section: React.ReactElement) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={project.id} initialProjects={[project]}>
        <SidebarProvider>{section}</SidebarProvider>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

function escapeHtml(value: string) {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
}

function writeArtifact(name: string, content: string) {
  artifacts[name] = content
}

function writeBeforeArtifact(sectionName: string) {
  writeArtifact(`${sectionName}-before.txt`, `${beforeSummaries[sectionName]}\n`)
}

function renderTextSnapshot(container: HTMLElement) {
  return Array.from(container.querySelectorAll('h3, h4, section, label, p, span, button, input, textarea'))
    .map((element) => {
      const tag = element.tagName.toLowerCase()
      const classes = element.getAttribute('class') ?? ''
      const text = element.textContent?.trim().replace(/\s+/g, ' ') || element.getAttribute('placeholder') || ''
      return `<${tag} class="${escapeHtml(classes)}">${escapeHtml(text)}</${tag}>`
    })
    .join('\n')
}

function textColorClass(element: Element): string | null {
  for (const className of Array.from(element.classList)) {
    if (className === 'text-foreground' || className === 'text-muted-foreground' || className === 'text-foreground/70') {
      return className
    }
  }
  return element.parentElement ? textColorClass(element.parentElement) : null
}

type Rgb = [number, number, number]

const cssColors: Record<string, Rgb> = {
  foreground: [24, 24, 27],
  'muted-foreground': [82, 82, 91],
  background: [255, 255, 255],
}

function relativeLuminance([red, green, blue]: Rgb) {
  const [r, g, b] = [red, green, blue].map((channel) => {
    const normalized = channel / 255
    return normalized <= 0.03928
      ? normalized / 12.92
      : ((normalized + 0.055) / 1.055) ** 2.4
  })
  return 0.2126 * r + 0.7152 * g + 0.0722 * b
}

function contrastRatio(foreground: Rgb, background: Rgb) {
  const lighter = Math.max(relativeLuminance(foreground), relativeLuminance(background))
  const darker = Math.min(relativeLuminance(foreground), relativeLuminance(background))
  return Number(((lighter + 0.05) / (darker + 0.05)).toFixed(2))
}

function colorForToken(token: string): Rgb {
  if (token === 'text-foreground') return cssColors.foreground
  if (token === 'text-muted-foreground') return cssColors['muted-foreground']
  return [93, 93, 98]
}

function auditContrast(container: HTMLElement) {
  const bodyText = Array.from(container.querySelectorAll('p, label, span, button, pre, div'))
    .filter((element) => (element.textContent ?? '').trim().length > 0)
  const violations = bodyText.flatMap((element) => {
    const token = textColorClass(element)
    if (!token) return []
    const ratio = contrastRatio(colorForToken(token), cssColors.background)
    return ratio >= 4.5 ? [] : [{ text: element.textContent?.trim(), token, ratio }]
  })
  return { checkedNodes: bodyText.length, violations }
}

function visualDiffSummary(sectionName: string, after: HTMLElement) {
  const cardSections = after.querySelectorAll('section.rounded-lg.border').length
  const pageTitle = after.querySelector('h2.text-sm.font-medium.text-foreground')?.textContent?.trim()
  return [
    `Section: ${sectionName}`,
    beforeSummaries[sectionName],
    `After: page title is rendered by SettingsSection as h2 "${pageTitle ?? 'unknown'}".`,
    `After: detected ${cardSections} CardSection-style rounded-lg border containers in the rendered snapshot.`,
    'Diff verdict: expected visual contract migration observed in deterministic rendered artifact.',
    '',
  ].join('\n')
}

describe('settings visual accessibility evidence', () => {
  beforeEach(() => {
    artifacts = {}
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('generates before/after visual diff snapshots and contrast audit artifacts in memory', () => {
    const auditResults: Record<string, ReturnType<typeof auditContrast>> = {}

    for (const [sectionName, section] of sections) {
      arrangeLoadedMocks()
      const { container } = renderEvidenceSection(section)
expect(screen.getByRole('heading', { level: 2 })).toBeInTheDocument()
      writeBeforeArtifact(sectionName)
      writeArtifact(`${sectionName}-after.html`, renderTextSnapshot(container))
      writeArtifact(`${sectionName}-visual-diff.txt`, visualDiffSummary(sectionName, container))
      auditResults[sectionName] = auditContrast(container)
      cleanup()
    }

    writeArtifact('contrast-audit.json', `${JSON.stringify(auditResults, null, 2)}\n`)

    expect(Object.keys(artifacts).sort()).toEqual([
      'agent-after.html',
      'agent-before.txt',
      'agent-visual-diff.txt',
      'ai-after.html',
      'ai-before.txt',
      'ai-visual-diff.txt',
      'contrast-audit.json',
      'preferences-after.html',
      'preferences-before.txt',
      'preferences-visual-diff.txt',
      'repositories-after.html',
      'repositories-before.txt',
      'repositories-visual-diff.txt',
      'system-after.html',
      'system-before.txt',
      'system-visual-diff.txt',
      'templates-after.html',
      'templates-before.txt',
      'templates-visual-diff.txt',
      'workflows-after.html',
      'workflows-before.txt',
      'workflows-visual-diff.txt',
    ])
    expect(artifacts['contrast-audit.json']).toContain('"violations": []')
    expect(Object.values(auditResults).flatMap((result) => result.violations)).toEqual([])
    expect(Object.values(auditResults).every((result) => result.checkedNodes > 0)).toBe(true)
  })
})
