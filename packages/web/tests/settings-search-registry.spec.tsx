import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { cleanup, render, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'

import { settingsSearchRegistry } from '../src/pages/settings/model/settings-search-registry'
import { AgentSettingsSection } from '../src/pages/settings/ui/AgentSettingsSection'
import { PreferencesSection } from '../src/pages/settings/ui/PreferencesSection'
import { RepositoriesSection } from '../src/pages/settings/ui/RepositoriesSection'
import { ProjectProvider } from '../src/entities/project'
import { SystemSettingsSection } from '../src/pages/settings/ui/SystemSettingsSection'
import { TemplatesSection } from '../src/pages/settings/ui/TemplatesSection'
import { WorkflowProfilesSection } from '../src/pages/settings/ui/WorkflowProfilesSection'
import { SidebarProvider } from '../src/shared/ui/components/sidebar'

const RUNTIME_CONFIG = {
  timeout: 1800000,
  stageTimeout: 3600000,
  taskTimeout: 600000,
  maxConcurrent: 8,
  maxGracePeriods: 2,
  pollInterval: 30000,
}

const GENERAL_CONFIG = {
  agentTimeout: 1800,
  taskTimeout: 600,
  stageTimeout: 3600,
  maxConcurrentAgents: 8,
  maxGracePeriods: 2,
  pollInterval: 30000,
  logLevel: 'INFO',
}

const SYSTEM_INFO = {
  running: {
    version: '1.0.0',
    gitHash: 'abc1234567890',
    startedAt: '2026-06-19T00:00:00.000Z',
  },
  source: {
    path: '/path/to/source',
    branch: 'master',
    head: 'abc1234567890',
    dirty: false,
  },
  install: {
    mode: 'binary' as const,
    reason: 'released',
    serviceManager: 'systemd',
    serverUnit: 'mohist-server.service',
    runnerUnit: 'mohist-runner.service',
  },
  update: {
    available: false,
    status: 'idle' as const,
    reason: null,
  },
  services: {
    server: 'active',
    runner: 'active',
  },
  paths: {
    db: '/var/lib/mohist/db.sqlite',
    config: '/etc/mohist/config.jsonc',
    opencode: '/usr/local/bin/opencode',
    logs: '~/.mohist/logs/',
  },
}

const REPOSITORIES = [
  {
    name: 'frontend',
    gitUrl: 'https://github.com/example/frontend.git',
    baseBranch: 'main',
    isDefault: true,
  },
]

const WORKFLOW_PROFILES = [
  {
    id: 'mohist/local',
    displayName: 'Default',
    description: 'Standard staged workflow.',
    isDefault: true,
    agentRuntime: 'opencode' as const,
  },
  {
    id: 'mohist/quick-fix',
    displayName: 'Quick Fix',
    description: 'Short repair workflow.',
    isDefault: false,
    agentRuntime: 'opencode' as const,
  },
]

function makeQueryClient() {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: Infinity },
      mutations: { retry: false },
    },
  })
  queryClient.setQueryData(['agent-runtime'], RUNTIME_CONFIG)
  queryClient.setQueryData(['config'], GENERAL_CONFIG)
  queryClient.setQueryData(['log-level'], { level: GENERAL_CONFIG.logLevel })
  queryClient.setQueryData(['repositories', 'proj-1'], REPOSITORIES)
  queryClient.setQueryData(['system-info'], SYSTEM_INFO)
  queryClient.setQueryData(['system-update-status'], { hasJob: false, job: null })
  queryClient.setQueryData(['workflow-profiles', 'proj-1'], WORKFLOW_PROFILES)
  for (const profile of WORKFLOW_PROFILES) {
    queryClient.setQueryData(['workflow-profile', 'proj-1', profile.id], {
      ...profile,
      projectId: 'proj-1',
      sourceProvenance: 'BuiltIn',
      isBuiltIn: true,
      definitionSource: `id: ${profile.id}`,
      yaml: `id: ${profile.id}`,
      stages: [],
    })
  }
  queryClient.setQueryData(['project-workflow-profile', 'proj-1'], {
    projectId: 'proj-1',
    defaultTemplateId: null,
    disabledWorkflowProfileIds: [],
  })
  queryClient.setQueryData(['project-templates', 'proj-1'], [])
  queryClient.setQueryData(['system-templates'], [])
  return queryClient
}

function renderSection(node: React.ReactNode) {
  const queryClient = makeQueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider
        initialProjectId="proj-1"
        initialProjects={[
          {
            id: 'proj-1',
            name: 'proj-1',
            createdAt: '2026-01-01T00:00:00Z',
            updatedAt: '2026-01-01T00:00:00Z',
            repositories: REPOSITORIES,
          },
        ]}
      >
        <MemoryRouter>
          <SidebarProvider>{node}</SidebarProvider>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
})

describe('settingsSearchRegistry', () => {
  it('aggregates every configurable field across the 6 existing tabs', () => {
    const tabs = new Set(settingsSearchRegistry.map((entry) => entry.tab))
    expect(tabs).toEqual(new Set(['scheduling', 'preferences', 'repositories', 'templates', 'system', 'workflows']))
  })

  it('includes the scheduling fields from AgentSettingsSection.FIELDS', () => {
    const schedulingEntries = settingsSearchRegistry.filter((entry) => entry.tab === 'scheduling')
    expect(schedulingEntries.map((entry) => entry.focusTargetId)).toEqual([
      'agent-runtime-timeout',
      'agent-runtime-stageTimeout',
      'agent-runtime-taskTimeout',
      'agent-runtime-maxConcurrent',
      'agent-runtime-pollInterval',
      'agent-runtime-maxGracePeriods',
    ])
  })

  it('includes the repository form-field descriptors', () => {
    const repoEntries = settingsSearchRegistry.filter((entry) => entry.tab === 'repositories')
    expect(repoEntries.map((entry) => entry.focusTargetId)).toEqual([
      'repository-add-name',
      'repository-add-branch',
      'repository-add-giturl',
    ])
  })

  it('includes the templates search descriptor', () => {
    const templateEntries = settingsSearchRegistry.filter((entry) => entry.tab === 'templates')
    expect(templateEntries.map((entry) => entry.focusTargetId)).toEqual(['templates-search', 'template-new-button'])
  })

  it('includes the system log-level descriptor', () => {
    const systemEntries = settingsSearchRegistry.filter((entry) => entry.tab === 'system')
    expect(systemEntries.map((entry) => entry.focusTargetId)).toEqual(['system-log-level', 'system-source-path'])
  })

  it('includes workflow-related entries for Settings Search discoverability', () => {
    const workflowEntries = settingsSearchRegistry.filter((entry) => entry.tab === 'workflows')
    expect(workflowEntries.length).toBeGreaterThan(0)
    for (const entry of workflowEntries) {
      expect(entry.label).toBeTruthy()
      expect(entry.description).toBeTruthy()
      expect(entry.focusTargetId).toBeTruthy()
    }
  })

  it('includes a Workflow Profiles entry that navigates to the section', () => {
    const profileEntry = settingsSearchRegistry.find((e) => e.focusTargetId === 'workflow-profiles-section')
    expect(profileEntry).toBeDefined()
    expect(profileEntry!.tab).toBe('workflows')
    expect(profileEntry!.label).toBe('Workflow Profiles')
  })

  it('includes a Project Default Workflow entry', () => {
    const defaultEntry = settingsSearchRegistry.find((e) => e.focusTargetId === 'project-default-workflow')
    expect(defaultEntry).toBeDefined()
    expect(defaultEntry!.tab).toBe('workflows')
    expect(defaultEntry!.label).toBe('Project Default Workflow')
  })

  it('includes the Preferences theme descriptor', () => {
    const preferencesEntries = settingsSearchRegistry.filter((entry) => entry.tab === 'preferences')
    expect(preferencesEntries.map((entry) => entry.focusTargetId)).toEqual(['preferences-theme'])
  })

  it('has no duplicate focusTargetId across the registry', () => {
    const ids = settingsSearchRegistry.map((entry) => entry.focusTargetId)
    const unique = new Set(ids)
    expect(unique.size).toBe(ids.length)
  })

  it('every entry has the required shape: tab, label, description, focusTargetId', () => {
    for (const entry of settingsSearchRegistry) {
      expect(entry.tab).toMatch(/^(scheduling|repositories|workflows|templates|system|preferences)$/)
      expect(typeof entry.label).toBe('string')
      expect(entry.label.length).toBeGreaterThan(0)
      expect(typeof entry.description).toBe('string')
      expect(entry.description.length).toBeGreaterThan(0)
      expect(typeof entry.focusTargetId).toBe('string')
      expect(entry.focusTargetId.length).toBeGreaterThan(0)
      if (entry.placeholder !== undefined) {
        expect(typeof entry.placeholder).toBe('string')
      }
    }
  })

  it('has no duplicate focusTargetId across the registry', () => {
    const ids = settingsSearchRegistry.map((entry) => entry.focusTargetId)
    // AgentSettingsSection.FIELDS focus ids are reused from agent-runtime-<key>.
    expect(new Set(ids).size).toBe(ids.length)
  })

  it('exposes the registry as a frozen, immutable list', () => {
    expect(Object.isFrozen(settingsSearchRegistry)).toBe(true)
  })
})

describe('settingsSearchRegistry focusTargetId resolves when each tab is rendered', () => {
  it('focusTargetId from the scheduling tab resolves to a focusable element', async () => {
    const { baseElement } = renderSection(<AgentSettingsSection />)

    await waitFor(() => {
      expect(baseElement.querySelector('#agent-runtime-timeout')).toBeTruthy()
    })
    const input = baseElement.querySelector<HTMLInputElement>('#agent-runtime-timeout')
    expect(input).not.toBeNull()
    expect(input?.tagName).toBe('INPUT')
    input?.focus()
    expect(document.activeElement).toBe(input)
  })

  it('every scheduling focus target resolves to an input', async () => {
    const { baseElement } = renderSection(<AgentSettingsSection />)

    const ids = [
      'agent-runtime-timeout',
      'agent-runtime-stageTimeout',
      'agent-runtime-taskTimeout',
      'agent-runtime-maxConcurrent',
      'agent-runtime-pollInterval',
      'agent-runtime-maxGracePeriods',
    ]
    await waitFor(() => {
      ids.forEach((id) => expect(baseElement.querySelector(`#${id}`)).toBeTruthy())
    })
    ids.forEach((id) => {
      const el = baseElement.querySelector<HTMLElement>(`#${id}`)
      expect(el).not.toBeNull()
      el?.focus()
      expect(document.activeElement).toBe(el)
    })
  })

  it('every repositories focus target resolves to a focusable element', () => {
    const { baseElement } = renderSection(<RepositoriesSection projectId="proj-1" />)

    const ids = ['repository-add-name', 'repository-add-branch', 'repository-add-giturl']
    ids.forEach((id) => {
      const el = baseElement.querySelector<HTMLElement>(`#${id}`)
      expect(el).not.toBeNull()
      expect(el?.tagName).toBe('INPUT')
      el?.focus()
      expect(document.activeElement).toBe(el)
    })
  })

  it('every templates focus target resolves to a focusable element', () => {
    const { baseElement } = renderSection(<TemplatesSection />)

    const el = baseElement.querySelector<HTMLElement>('#templates-search')
    expect(el).not.toBeNull()
    expect(el?.tagName).toBe('INPUT')
    el?.focus()
    expect(document.activeElement).toBe(el)

    const newButton = baseElement.querySelector<HTMLElement>('#template-new-button')
    expect(newButton).not.toBeNull()
    expect(newButton?.tagName).toBe('BUTTON')
    newButton?.focus()
    expect(document.activeElement).toBe(newButton)
  })

  it('the system focus target resolves to a focusable element', async () => {
    const { baseElement } = renderSection(<SystemSettingsSection />)
    await waitFor(() => {
      expect(baseElement.querySelector('#system-log-level')).toBeTruthy()
    })
    const logLevel = baseElement.querySelector<HTMLElement>('#system-log-level')
    expect(logLevel).not.toBeNull()
    expect(logLevel?.tagName).toBe('BUTTON')
    logLevel?.focus()
    expect(document.activeElement).toBe(logLevel)

    const sourcePath = baseElement.querySelector<HTMLElement>('#system-source-path')
    expect(sourcePath).not.toBeNull()
    sourcePath?.focus()
    expect(document.activeElement).toBe(sourcePath)
  })

  it('the preferences theme focus target resolves to a focusable element', async () => {
    const { baseElement } = renderSection(<PreferencesSection />)
    await waitFor(() => {
      expect(baseElement.querySelector('#preferences-theme')).toBeTruthy()
    })
    const el = baseElement.querySelector<HTMLElement>('#preferences-theme')
    expect(el).not.toBeNull()
    el?.focus()
    expect(document.activeElement).toBe(el)
  })

  it('every focusTargetId across the registry resolves to a real element when its tab is rendered', async () => {
    const rendered: Array<{ tab: string; element: HTMLElement }> = []

    const tabSegments: Array<{
      tab: string
      node: React.ReactNode
    }> = [
      { tab: 'scheduling', node: <AgentSettingsSection /> },
      { tab: 'preferences', node: <PreferencesSection /> },
      { tab: 'repositories', node: <RepositoriesSection projectId="proj-1" /> },
      { tab: 'workflows', node: <WorkflowProfilesSection /> },
      { tab: 'templates', node: <TemplatesSection /> },
      { tab: 'system', node: <SystemSettingsSection /> },
    ]

    for (const segment of tabSegments) {
      cleanup()
      const { baseElement } = renderSection(segment.node)
      await new Promise((resolve) => setTimeout(resolve, 0))
      const entries = settingsSearchRegistry.filter((entry) => entry.tab === segment.tab)
      for (const entry of entries) {
        const el = baseElement.querySelector<HTMLElement>(`#${entry.focusTargetId}`)
        expect(el, `missing #${entry.focusTargetId} on tab ${segment.tab}`).not.toBeNull()
        rendered.push({ tab: segment.tab, element: el! })
      }
    }

    expect(rendered.length).toBe(settingsSearchRegistry.length)
  })

  it('entries for conditionally mounted targets declare reveal events', () => {
    const repositoryEntries = settingsSearchRegistry.filter((entry) =>
      entry.focusTargetId.startsWith('repository-add-'),
    )
    expect(repositoryEntries.length).toBe(3)
    repositoryEntries.forEach((entry) => {
      expect(entry.revealEvent).toBe('mohist:settings:reveal-repository-add-form')
    })
  })
})
