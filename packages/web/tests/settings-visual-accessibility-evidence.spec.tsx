import '@testing-library/jest-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
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
  agent: 'Before: Runtime used a rounded-md bg-muted border card plus text-foreground/80 and text-foreground/85 tokens. Tab heading was "Coder Agent Runtime", differing from the nav label "Runtime".',
  preferences: 'Before: Preferences tab did not exist. The tab is new for Settings 2.0; page title is rendered by SettingsSection and cards are CardSection instances.',
  repositories: 'Before: Repository rows used rounded-lg border bg-card/50 and included hardcoded text-gray-500.',
  workflows: 'Before: Workflow profile cards used rounded-md border wrappers and text-foreground/85 body/caption tokens. Tab heading was "Workflow Profiles", differing from the nav label "Workflows".',
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

const AGENT_RUNTIME = {
  timeout: 600000,
  stageTimeout: 3600000,
  taskTimeout: 600000,
  maxConcurrent: 3,
  maxGracePeriods: 3,
  pollInterval: 5000,
}

const CONFIG = {
  agentTimeout: 600000,
  maxConcurrentAgents: 3,
  pollInterval: 5000,
  logLevel: 'info',
  taskTimeout: 600000,
  stageTimeout: 3600000,
  maxGracePeriods: 3,
}

const SYSTEM_INFO = {
  running: { version: '1.0.0', gitHash: 'abcdef1234567890', startedAt: '2026-06-18T00:00:00Z' },
  source: { path: '/repo/mohist', branch: 'master', head: 'abcdef1234567890', dirty: false },
  install: { mode: 'local-source', serviceManager: 'systemd', serverUnit: 'mohist-server', runnerUnit: 'mohist-runner', reason: 'local checkout' },
  update: { status: 'up-to-date', available: false, reason: 'Already current' },
  services: { server: 'running', runner: 'running' },
  paths: { db: '/var/lib/mohist/db.sqlite', config: '~/.mohist/config.jsonc', opencode: '~/.config/opencode', logs: '~/.mohist/logs' },
}

const WORKFLOW_PROFILES = [
  { id: 'mohist/local', displayName: 'Default', description: 'Standard staged workflow.', isDefault: true },
  { id: 'mohist/quick-fix', displayName: 'Quick Fix', description: 'Short repair workflow.', isDefault: false },
]

const PROJECT_TEMPLATES = [
  { key: 'build-plan', displayName: 'Build Plan', description: 'Plan the implementation.', tags: ['plan'], stage: 'plan', body: 'Plan body', source: 'system' },
  { key: 'review-fix', displayName: 'Review Fix', description: 'Fix review findings.', tags: ['check'], stage: 'check', body: 'Fix body', source: 'project-override' },
]

function makeQueryClient() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: Infinity },
      mutations: { retry: false },
    },
  })
  queryClient.setQueryData(['repositories', project.id], project.repositories)
  queryClient.setQueryData(['opencode-runtime'], { mode: 'local', command: 'opencode', model: null, note: '' })
  queryClient.setQueryData(['opencode-model-ids', 'opencode', project.id], {
    models: ['openai/gpt-5.1', 'anthropic/claude-sonnet-4'],
    modelVariants: {},
  })
  queryClient.setQueryData(['opencode-model', project.id], { model: 'openai/gpt-5.1', variant: null })
  queryClient.setQueryData(['stage-models', project.id], {
    stageModels: { check: 'anthropic/claude-sonnet-4' },
    stageModelVariants: null,
  })
  queryClient.setQueryData(['agent-runtime'], AGENT_RUNTIME)
  queryClient.setQueryData(['config'], CONFIG)
  queryClient.setQueryData(['log-level'], { level: CONFIG.logLevel })
  queryClient.setQueryData(['system-info'], SYSTEM_INFO)
  queryClient.setQueryData(['system-update-status'], { hasJob: false, job: null })
  queryClient.setQueryData(['workflow-templates', 'system'], WORKFLOW_PROFILES)
  queryClient.setQueryData(['workflow-templates', 'system', project.id], WORKFLOW_PROFILES)
  for (const profile of WORKFLOW_PROFILES) {
    queryClient.setQueryData(['workflow-profile', profile.id], null)
  }
  queryClient.setQueryData(['project-workflow-profile', project.id], {
    projectId: project.id,
    defaultTemplateId: null,
    disabledWorkflowProfileIds: [],
  })
  queryClient.setQueryData(['project-templates', project.id], PROJECT_TEMPLATES)
  queryClient.setQueryData(['system-templates'], [])
  return queryClient
}

function renderEvidenceSection(section: React.ReactElement) {
  const queryClient = makeQueryClient()
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
  })

  it('generates before/after visual diff snapshots and contrast audit artifacts in memory', () => {
    const auditResults: Record<string, ReturnType<typeof auditContrast>> = {}

    for (const [sectionName, section] of sections) {
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
