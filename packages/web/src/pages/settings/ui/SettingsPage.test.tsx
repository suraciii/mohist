import '@testing-library/jest-dom'
import { http, HttpResponse } from 'msw'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Project, Repository } from '../../../entities/project'
import { ProjectProvider } from '../../../entities/project'
import { SidebarProvider } from '@/shared/ui/components/sidebar'
import type { GeneralConfig } from '../../../entities/settings'
import { SettingsPage } from './SettingsPage'
import { useMswServer } from '../../../../tests/support/msw'
import { setScopedProperty } from '../../../../tests/support/scoped-property'

const DEFAULT_CONFIG: GeneralConfig = {
  agentTimeout: 600,
  taskTimeout: 600,
  stageTimeout: 3600,
  maxConcurrentAgents: 3,
  maxGracePeriods: 3,
  pollInterval: 5000,
  logLevel: 'INFO',
}

let _repositories: Repository[] = []
let _configData: GeneralConfig = { ...DEFAULT_CONFIG }
const reposUrlCaptures: string[] = []

useMswServer(
  http.get('/api/projects/:projectId/repositories', ({ request }) => {
    reposUrlCaptures.push(request.url)
    return HttpResponse.json({ success: true, data: _repositories })
  }),
  http.get('/api/config', () => {
    return HttpResponse.json({ success: true, data: _configData })
  }),
  http.put('/api/config/:key', () => {
    return HttpResponse.json({ success: true, data: _configData })
  }),
  http.get('/api/opencode/runtime', () =>
    HttpResponse.json({
      success: true,
      data: { mode: 'local-opencode', command: 'opencode', model: null, note: '' },
    }),
  ),
  http.get('/api/projects/:projectId/opencode/models', () =>
    HttpResponse.json({ success: true, data: { models: [], modelVariants: {} } }),
  ),
  http.get('/api/projects/:projectId/workflow-profiles', ({ params }) =>
    HttpResponse.json({
      success: true,
      data: [{
        projectId: params.projectId,
        profileId: 'mohist/local',
        name: 'Mohist Local',
        description: 'Standard staged workflow.',
        sourceProvenance: 'BuiltIn',
        isBuiltIn: true,
        definitionSource: null,
        agentRuntime: 'opencode',
      }],
    }),
  ),
  http.get('/api/projects/:projectId/workflow-profile/default', ({ params }) =>
    HttpResponse.json({
      success: true,
      data: {
        projectId: params.projectId,
        defaultWorkflowProfileId: 'mohist/local',
        disabledWorkflowProfileIds: [],
      },
    }),
  ),
  http.get('/api/projects/:projectId/variables', () =>
    HttpResponse.json({ success: true, data: { vars: null, stages: null } }),
  ),
  http.get('/api/projects/:projectId/inbox/subscription', () =>
    HttpResponse.json({
      success: true,
      data: {
        workflow_failed: true,
        approval_requested: true,
        issue_started: true,
        issue_completed: true,
      },
    }),
  ),
  http.get('/api/system/info', () =>
    HttpResponse.json({
      success: true,
      data: {
        running: { version: '1.0.0', gitHash: 'test-hash', startedAt: '2026-01-01T00:00:00Z' },
        source: { path: '/repo', branch: 'master', head: 'test-hash', dirty: false },
        install: {
          mode: 'local-source',
          serviceManager: null,
          serverUnit: null,
          runnerUnit: null,
          reason: null,
        },
        update: { status: 'up-to-date', available: false, reason: null },
        services: { server: 'active', runner: 'active' },
        paths: { db: '/db', config: '/config', opencode: '/opencode', logs: '/logs' },
      },
    }),
  ),
  http.get('/api/system/update/status', () =>
    HttpResponse.json({ success: true, data: { hasJob: false, job: null } }),
  ),
)

beforeEach(() => {
  _repositories = []
  _configData = { ...DEFAULT_CONFIG }
  reposUrlCaptures.length = 0
})

const projects: Project[] = [
  {
    id: 'proj-first',
    name: 'first-project',
    repositories: [
      { name: 'first', gitUrl: 'git@example.com:first.git', baseBranch: 'main', isDefault: true },
    ],
    createdAt: '2026-06-01T00:00:00Z',
    updatedAt: '2026-06-01T00:00:00Z',
  },
  {
    id: 'proj-selected',
    name: 'selected-project',
    repositories: [
      { name: 'selected', gitUrl: 'git@example.com:selected.git', baseBranch: 'master', isDefault: true },
    ],
    createdAt: '2026-06-01T00:00:00Z',
    updatedAt: '2026-06-01T00:00:00Z',
  },
]

function renderSettings(initialEntry = '/settings/repositories') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="proj-selected" initialProjects={projects}>
        <SidebarProvider>
          <MemoryRouter initialEntries={[initialEntry]}>
            <Routes>
              <Route path="/settings/:section" element={<SettingsPage />} />
              <Route path="/:projectName/settings/:section" element={<SettingsPage />} />
            </Routes>
          </MemoryRouter>
        </SidebarProvider>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('SettingsPage sub-navigation', () => {
  afterEach(() => {
    cleanup()
    window.localStorage.clear()
    vi.clearAllMocks()
  })

  it('loads repositories for the selected project instead of the first project', async () => {
    _repositories = projects[1].repositories

    renderSettings()

    await waitFor(() => {
      expect(reposUrlCaptures.some((u) => u.includes('/projects/proj-selected/repositories'))).toBe(true)
    })
    expect(screen.getAllByText('selected').length).toBeGreaterThan(0)
    expect(screen.queryByText('first')).not.toBeInTheDocument()
  })

  it('renders a left sub-navigation grouped into Application and Project sections (no horizontal tab bar)', () => {
    renderSettings('/settings/ai')

    const subnav = screen.getByTestId('settings-subnav')
    expect(subnav).toBeInTheDocument()

    const applicationGroup = screen.getByTestId('settings-subnav-group-application')
    const projectGroup = screen.getByTestId('settings-subnav-group-project')

    expect(within(applicationGroup).getByTestId('settings-subnav-ai')).toBeInTheDocument()
    expect(within(applicationGroup).getByTestId('settings-subnav-agent')).toBeInTheDocument()
    expect(within(applicationGroup).getByTestId('settings-subnav-system')).toBeInTheDocument()
    expect(within(applicationGroup).getByTestId('settings-subnav-preferences')).toBeInTheDocument()

    expect(within(projectGroup).getByTestId('settings-subnav-repositories')).toBeInTheDocument()
    expect(within(projectGroup).getByTestId('settings-subnav-templates')).toBeInTheDocument()
    expect(within(projectGroup).getByTestId('settings-subnav-label-catalog')).toBeInTheDocument()
    expect(within(projectGroup).getByTestId('settings-subnav-workflows')).toBeInTheDocument()
    expect(within(projectGroup).getByTestId('settings-subnav-inbox')).toBeInTheDocument()

    expect(screen.queryByTestId('settings-tab-ai')).not.toBeInTheDocument()
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument()
  })

  it('renders each navigation group as a semantic list of links', () => {
    renderSettings('/settings/ai')

    const applicationList = screen.getByRole('list', { name: 'Application' })
    const projectList = screen.getByRole('list', { name: 'Project' })

    expect(within(applicationList).getAllByRole('listitem')).toHaveLength(4)
    expect(within(projectList).getAllByRole('listitem')).toHaveLength(5)
    expect(within(applicationList).getByRole('link', { name: 'Coder Agent' })).toBeInTheDocument()
    expect(within(projectList).getByRole('link', { name: 'Repositories' })).toBeInTheDocument()
  })

  it('application items link to /settings/<section> without a project name segment', () => {
    renderSettings('/settings/ai')

    expect(screen.getByTestId('settings-subnav-ai')).toHaveAttribute('href', '/settings/ai')
    expect(screen.getByTestId('settings-subnav-agent')).toHaveAttribute('href', '/settings/agent')
    expect(screen.getByTestId('settings-subnav-system')).toHaveAttribute('href', '/settings/system')
    expect(screen.getByTestId('settings-subnav-preferences')).toHaveAttribute(
      'href',
      '/settings/preferences',
    )
  })

  it('project items link to /:projectName/settings/<section> with the selected project name segment', () => {
    renderSettings('/settings/ai')

    expect(screen.getByTestId('settings-subnav-repositories')).toHaveAttribute(
      'href',
      '/selected-project/settings/repositories',
    )
    expect(screen.getByTestId('settings-subnav-templates')).toHaveAttribute(
      'href',
      '/selected-project/settings/templates',
    )
    expect(screen.getByTestId('settings-subnav-label-catalog')).toHaveAttribute(
      'href',
      '/selected-project/settings/label-catalog',
    )
    expect(screen.getByTestId('settings-subnav-workflows')).toHaveAttribute(
      'href',
      '/selected-project/settings/workflows',
    )
    expect(screen.getByTestId('settings-subnav-inbox')).toHaveAttribute(
      'href',
      '/selected-project/settings/inbox',
    )
  })

  it('marks only the active sub-nav item with aria-current="page"', () => {
    renderSettings('/settings/agent')

    expect(screen.getByTestId('settings-subnav-agent')).toHaveAttribute('aria-current', 'page')
    for (const key of [
      'ai',
      'system',
      'preferences',
      'repositories',
      'templates',
      'label-catalog',
      'workflows',
      'inbox',
    ]) {
      expect(screen.getByTestId(`settings-subnav-${key}`)).not.toHaveAttribute('aria-current')
    }
  })

  it('active aria-current follows the URL when mounted on a project-scoped section', () => {
    renderSettings('/selected-project/settings/repositories')

    expect(screen.getByTestId('settings-subnav-repositories')).toHaveAttribute(
      'aria-current',
      'page',
    )
    expect(screen.getByTestId('settings-subnav-ai')).not.toHaveAttribute('aria-current')
  })

  it('clicking an application item navigates to its /settings/<section> route', () => {
    renderSettings('/settings/ai')

    fireEvent.click(screen.getByTestId('settings-subnav-preferences'))

    expect(screen.getByTestId('preferences-theme-card')).toBeInTheDocument()
    expect(screen.getByTestId('settings-subnav-preferences')).toHaveAttribute('aria-current', 'page')
  })

  it('clicking a project item navigates to its /:projectName/settings/<section> route', async () => {
    renderSettings('/settings/ai')

    fireEvent.click(screen.getByTestId('settings-subnav-repositories'))

    await waitFor(() => {
      expect(reposUrlCaptures.some((u) => u.includes('/projects/proj-selected/repositories'))).toBe(true)
    })
    expect(screen.getByTestId('settings-subnav-repositories')).toHaveAttribute(
      'aria-current',
      'page',
    )
    expect(screen.getByTestId('repositories-section')).toBeInTheDocument()
  })

  it('ArrowDown moves roving focus from the active item to the next item and updates tabIndex', () => {
    renderSettings('/settings/ai')

    const ai = screen.getByTestId('settings-subnav-ai')
    const agent = screen.getByTestId('settings-subnav-agent')
    ai.focus()
    fireEvent.keyDown(ai, { key: 'ArrowDown' })

    expect(agent).toHaveFocus()
    expect(agent).toHaveProperty('tabIndex', 0)
    expect(ai).toHaveProperty('tabIndex', -1)
  })

  it('ArrowDown moves from the last Application item to the first Project item', () => {
    renderSettings('/settings/preferences')

    const preferences = screen.getByTestId('settings-subnav-preferences')
    const repositories = screen.getByTestId('settings-subnav-repositories')
    preferences.focus()
    fireEvent.keyDown(preferences, { key: 'ArrowDown' })

    expect(repositories).toHaveFocus()
    expect(repositories).toHaveProperty('tabIndex', 0)
    expect(preferences).toHaveProperty('tabIndex', -1)
  })

  it('ArrowUp wraps from the first item to the last item across the full subnav', () => {
    renderSettings('/settings/ai')

    const ai = screen.getByTestId('settings-subnav-ai')
    ai.focus()
    fireEvent.keyDown(ai, { key: 'ArrowUp' })

    expect(screen.getByTestId('settings-subnav-inbox')).toHaveFocus()
    expect(screen.getByTestId('settings-subnav-inbox')).toHaveProperty('tabIndex', 0)
    expect(ai).toHaveProperty('tabIndex', -1)
  })

  it('ArrowDown wraps from the last item to the first item across the full subnav', () => {
    renderSettings('/selected-project/settings/inbox')

    const inbox = screen.getByTestId('settings-subnav-inbox')
    inbox.focus()
    fireEvent.keyDown(inbox, { key: 'ArrowDown' })

    expect(screen.getByTestId('settings-subnav-ai')).toHaveFocus()
    expect(screen.getByTestId('settings-subnav-ai')).toHaveProperty('tabIndex', 0)
    expect(inbox).toHaveProperty('tabIndex', -1)
  })

  it('only the active item has tabIndex=0; the others are -1', () => {
    renderSettings('/settings/system')

    const indices: Record<string, number> = {
      ai: screen.getByTestId('settings-subnav-ai').tabIndex,
      agent: screen.getByTestId('settings-subnav-agent').tabIndex,
      system: screen.getByTestId('settings-subnav-system').tabIndex,
      preferences: screen.getByTestId('settings-subnav-preferences').tabIndex,
      repositories: screen.getByTestId('settings-subnav-repositories').tabIndex,
      inbox: screen.getByTestId('settings-subnav-inbox').tabIndex,
    }

    expect(indices.system).toBe(0)
    for (const [key, value] of Object.entries(indices)) {
      if (key === 'system') continue
      expect(value).toBe(-1)
    }
  })

  it('does not render the onboarding banner on the Coder Agent section', () => {
    renderSettings('/settings/ai')

    expect(screen.queryByTestId('settings-onboarding-banner')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /dismiss onboarding banner/i })).not.toBeInTheDocument()
    expect(screen.queryByText(/start here — select the coder agent model/i)).not.toBeInTheDocument()
  })

  it('redirects an invalid section to /settings/ai (application-scope fallback)', () => {
    renderSettings('/settings/not-a-real-section')

    expect(screen.getByTestId('settings-subnav-ai')).toHaveAttribute('aria-current', 'page')
    expect(screen.queryByTestId('settings-onboarding-banner')).not.toBeInTheDocument()
  })
})

describe('SettingsSubNav overflow affordance', () => {
  afterEach(() => {
    cleanup()
  })

  function withSimulatedOverflow(
    { scrollHeight, clientHeight }: { scrollHeight: number; clientHeight: number },
    run: () => void,
  ) {
    setScopedProperty(HTMLElement.prototype, 'scrollHeight', {
      configurable: true,
      get() {
        return scrollHeight
      },
    })
    setScopedProperty(HTMLElement.prototype, 'clientHeight', {
      configurable: true,
      get() {
        return clientHeight
      },
    })
    run()
  }

  it('does not render a fade affordance when the sub-nav content fits (no overflow)', () => {
    withSimulatedOverflow({ scrollHeight: 200, clientHeight: 400 }, () => {
      renderSettings('/settings/ai')

      const subnav = screen.getByTestId('settings-subnav')
      expect(subnav.getAttribute('data-overflow')).toBe('contained')
      expect(screen.queryByTestId('settings-subnav-fade-top')).not.toBeInTheDocument()
      expect(screen.queryByTestId('settings-subnav-fade-bottom')).not.toBeInTheDocument()
    })
  })

  it('renders the bottom fade affordance when the sub-nav content overflows', () => {
    withSimulatedOverflow({ scrollHeight: 1200, clientHeight: 400 }, () => {
      renderSettings('/settings/ai')

      const subnav = screen.getByTestId('settings-subnav')
      expect(subnav.getAttribute('data-overflow')).toBe('overflowing')
      expect(screen.getByTestId('settings-subnav-fade-bottom')).toBeInTheDocument()
      expect(screen.getByTestId('settings-subnav-fade-bottom')).toHaveAttribute('data-visible', 'true')
    })
  })

  it('toggles the top fade on once the user has scrolled away from the top of an overflowing list', () => {
    withSimulatedOverflow({ scrollHeight: 1200, clientHeight: 400 }, () => {
      renderSettings('/settings/ai')

      const subnav = screen.getByTestId('settings-subnav') as HTMLElement
      expect(screen.getByTestId('settings-subnav-fade-top')).toHaveAttribute('data-visible', 'false')

      subnav.scrollTop = 100
      fireEvent.scroll(subnav)

      expect(screen.getByTestId('settings-subnav-fade-top')).toHaveAttribute('data-visible', 'true')
      expect(screen.getByTestId('settings-subnav-fade-bottom')).toHaveAttribute('data-visible', 'true')
    })
  })

  it('hides the bottom fade once the user has scrolled to the bottom of an overflowing list', () => {
    withSimulatedOverflow({ scrollHeight: 1200, clientHeight: 400 }, () => {
      renderSettings('/settings/ai')

      const subnav = screen.getByTestId('settings-subnav') as HTMLElement
      subnav.scrollTop = subnav.scrollHeight - subnav.clientHeight
      fireEvent.scroll(subnav)

      expect(screen.getByTestId('settings-subnav-fade-top')).toHaveAttribute('data-visible', 'true')
      expect(screen.getByTestId('settings-subnav-fade-bottom')).toHaveAttribute(
        'data-visible',
        'false',
      )
    })
  })

  it('drops the fades after the measured overflow state transitions back to contained (resize)', () => {
    withSimulatedOverflow({ scrollHeight: 1200, clientHeight: 400 }, () => {
      renderSettings('/settings/ai')
      expect(screen.getByTestId('settings-subnav-fade-bottom')).toBeInTheDocument()
    })

    setScopedProperty(HTMLElement.prototype, 'scrollHeight', {
      configurable: true,
      get() {
        return 200
      },
    })

    // Triggering a scroll event runs the same measure() handler the hook
    // attaches; the resize path runs the same handler. We just need to
    // exercise it once with the new layout values.
    const subnav = screen.getByTestId('settings-subnav') as HTMLElement
    fireEvent.scroll(subnav)

    expect(screen.queryByTestId('settings-subnav-fade-bottom')).not.toBeInTheDocument()
    expect(screen.queryByTestId('settings-subnav-fade-top')).not.toBeInTheDocument()
  })
})

function getInputByLabel(label: string): HTMLInputElement {
  const labelEl = screen.getByText(label).closest('label')
  if (!labelEl) throw new Error(`No label for ${label}`)
  const wrapper = labelEl.parentElement
  if (!wrapper) throw new Error(`No wrapper for label ${label}`)
  const input = wrapper.querySelector('input')
  if (!input) throw new Error(`No input for label ${label}`)
  return input as HTMLInputElement
}

describe('SettingsSubNav dirty-guard', () => {
  beforeEach(() => {
    _configData = { ...DEFAULT_CONFIG }
    _repositories = []
    reposUrlCaptures.length = 0
  })

  afterEach(() => {
    cleanup()
    window.localStorage.clear()
    vi.clearAllMocks()
  })

  it('proceeds with sub-nav navigation when the Agent form is clean (no prompt)', async () => {
    renderSettings('/settings/agent')

    await waitFor(() => {
      expect(screen.getByText('Session Timeout')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByTestId('settings-subnav-preferences'))

    await waitFor(() => {
      expect(screen.getByTestId('preferences-theme-card')).toBeInTheDocument()
    })
    expect(screen.getByTestId('settings-subnav-preferences')).toHaveAttribute('aria-current', 'page')
    expect(screen.queryByTestId('settings-dirty-discard-alert')).not.toBeInTheDocument()
  })

  it('opens the discard dialog when selecting another tab while the Agent form is dirty', async () => {
    renderSettings('/settings/agent')

    await waitFor(() => {
      expect(screen.getByText('Session Timeout')).toBeInTheDocument()
    })

    const sessionInput = getInputByLabel('Session Timeout')
    fireEvent.change(sessionInput, { target: { value: '42' } })

    fireEvent.click(screen.getByTestId('settings-subnav-preferences'))

    const dialog = await screen.findByTestId('settings-dirty-discard-alert')
    expect(dialog).toBeInTheDocument()
    expect(within(dialog).getByText('Discard unsaved changes?')).toBeInTheDocument()
    expect(within(dialog).getByText(/You have unsaved changes/i)).toBeInTheDocument()
    expect(within(dialog).getByTestId('settings-dirty-discard-alert-confirm')).toBeInTheDocument()
    expect(within(dialog).getByTestId('settings-dirty-discard-alert-cancel')).toBeInTheDocument()

    expect(screen.getByTestId('settings-subnav-agent')).toHaveAttribute('aria-current', 'page')
    expect(screen.queryByTestId('preferences-theme-card')).not.toBeInTheDocument()
  })

  it('keeps the dirty form intact when the discard dialog is cancelled', async () => {
    renderSettings('/settings/agent')

    await waitFor(() => {
      expect(screen.getByText('Session Timeout')).toBeInTheDocument()
    })

    const sessionInput = getInputByLabel('Session Timeout') as HTMLInputElement
    fireEvent.change(sessionInput, { target: { value: '42' } })
    expect(sessionInput.value).toBe('42')

    fireEvent.click(screen.getByTestId('settings-subnav-preferences'))

    const dialog = await screen.findByTestId('settings-dirty-discard-alert')
    fireEvent.click(within(dialog).getByTestId('settings-dirty-discard-alert-cancel'))

    await waitFor(() => {
      expect(screen.queryByTestId('settings-dirty-discard-alert')).not.toBeInTheDocument()
    })

    expect(screen.getByTestId('settings-subnav-agent')).toHaveAttribute('aria-current', 'page')
    expect(screen.queryByTestId('preferences-theme-card')).not.toBeInTheDocument()

    const sessionInputAfter = getInputByLabel('Session Timeout') as HTMLInputElement
    expect(sessionInputAfter.value).toBe('42')
  })

  it('navigates to the requested tab after the dirty dialog is confirmed', async () => {
    renderSettings('/settings/agent')

    await waitFor(() => {
      expect(screen.getByText('Session Timeout')).toBeInTheDocument()
    })

    const sessionInput = getInputByLabel('Session Timeout')
    fireEvent.change(sessionInput, { target: { value: '42' } })

    fireEvent.click(screen.getByTestId('settings-subnav-preferences'))

    const dialog = await screen.findByTestId('settings-dirty-discard-alert')
    fireEvent.click(within(dialog).getByTestId('settings-dirty-discard-alert-confirm'))

    await waitFor(() => {
      expect(screen.getByTestId('preferences-theme-card')).toBeInTheDocument()
    })
    expect(screen.getByTestId('settings-subnav-preferences')).toHaveAttribute('aria-current', 'page')
    expect(screen.queryByTestId('settings-dirty-discard-alert')).not.toBeInTheDocument()
  })

  it('clears dirty state after navigating away so the next tab switch is unguarded', async () => {
    renderSettings('/settings/agent')

    await waitFor(() => {
      expect(screen.getByText('Session Timeout')).toBeInTheDocument()
    })

    const sessionInput = getInputByLabel('Session Timeout')
    fireEvent.change(sessionInput, { target: { value: '42' } })

    fireEvent.click(screen.getByTestId('settings-subnav-preferences'))

    const dialog = await screen.findByTestId('settings-dirty-discard-alert')
    fireEvent.click(within(dialog).getByTestId('settings-dirty-discard-alert-confirm'))

    await waitFor(() => {
      expect(screen.getByTestId('preferences-theme-card')).toBeInTheDocument()
    })

    fireEvent.click(screen.getByTestId('settings-subnav-system'))

    await waitFor(() => {
      expect(screen.getByTestId('settings-subnav-system')).toHaveAttribute('aria-current', 'page')
    })
    expect(screen.queryByTestId('settings-dirty-discard-alert')).not.toBeInTheDocument()
  })
})
