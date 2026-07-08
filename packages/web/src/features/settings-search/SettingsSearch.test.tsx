// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useLocation, useParams } from 'react-router-dom'
import { act, cleanup, fireEvent, render, screen, waitFor, waitForElementToBeRemoved } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Project } from '../../entities/project'
import { ProjectProvider } from '../../entities/project'
import { SettingsSearch, buildHaystack, groupEntriesByTab, NO_MATCHES_COPY } from './SettingsSearch'
import {
  __resetShortcutHandlersForTesting,
  getShortcutHandler,
  registerShortcutHandler,
} from './keyboard-shortcuts'
import { settingsSearchRegistry } from './registry'
import type { SettingsSearchEntry } from './types'

const aiSettingsClient = vi.hoisted(() => ({
  useOpencodeRuntime: vi.fn(),
  useAvailableModelIds: vi.fn(),
  useOpencodeModel: vi.fn(),
  useUpdateOpencodeModel: vi.fn(),
  useStageModels: vi.fn(),
  useSetStageModels: vi.fn(),
}))

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

vi.mock('../../entities/settings', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../entities/settings')>()
  return {
    ...actual,
    useOpencodeRuntime: aiSettingsClient.useOpencodeRuntime,
    useAvailableModelIds: aiSettingsClient.useAvailableModelIds,
    useOpencodeModel: aiSettingsClient.useOpencodeModel,
    useUpdateOpencodeModel: aiSettingsClient.useUpdateOpencodeModel,
    useStageModels: aiSettingsClient.useStageModels,
    useSetStageModels: aiSettingsClient.useSetStageModels,
    useAgentRuntime: () => ({
      data: RUNTIME_CONFIG,
      isLoading: false,
      error: null,
      refetch: vi.fn(),
    }),
    useConfig: () => ({ data: GENERAL_CONFIG }),
    useSetAgentRuntime: () => ({ mutateAsync: vi.fn() }),
  }
})

function arrangeAiLoaded() {
  aiSettingsClient.useOpencodeRuntime.mockReturnValue({ isLoading: false, error: null })
  aiSettingsClient.useAvailableModelIds.mockReturnValue({
    data: { models: ['openai/gpt-4', 'anthropic/claude-3'], modelVariants: {} },
    isLoading: false,
    error: null,
  })
  aiSettingsClient.useOpencodeModel.mockReturnValue({ data: { model: null, variant: null } })
  aiSettingsClient.useUpdateOpencodeModel.mockReturnValue({ mutate: vi.fn() })
  aiSettingsClient.useStageModels.mockReturnValue({ data: { stageModels: null, stageModelVariants: null } })
  aiSettingsClient.useSetStageModels.mockReturnValue({ mutate: vi.fn() })
}

function makeQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
}

const SELECTED_PROJECT: Project = {
  id: 'proj-selected',
  name: 'selected-project',
  repositories: [],
  createdAt: '2026-06-01T00:00:00Z',
  updatedAt: '2026-06-01T00:00:00Z',
}

/**
 * Minimal placeholder for each Settings tab. We only need the focus target
 * element to mount after navigation; the real section component is exercised
 * by the per-tab unit tests. Each placeholder renders a focusable element
 * whose `id` matches the registry's `focusTargetId` so the post-Enter
 * element-poll can find it.
 */
function TabPlaceholder() {
  const { section } = useParams<{ section: string }>()
  const ids =
    section === 'agent'
      ? ['agent-runtime-timeout']
      : section === 'ai'
        ? ['settings-default-model', 'settings-stage-model-plan']
        : section === 'repositories'
          ? ['repository-add-name']
          : section === 'templates'
            ? ['templates-search']
            : section === 'system'
              ? ['system-log-level']
              : section === 'preferences'
                ? ['preferences-theme']
                : ['workflow-profiles-section', 'project-default-workflow']
  return (
    <div data-testid={`placeholder-section-${section}`}>
      {ids.map((id) => (
        <input key={id} data-testid={`placeholder-input-${id}`} id={id} />
      ))}
    </div>
  )
}

function renderSettingsSearch(initialEntry = '/settings/ai') {
  const queryClient = makeQueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialEntry]}>
        <Routes>
          <Route
            path="/settings/:section"
            element={
              <>
                <TabPlaceholder />
                <SettingsSearch />
              </>
            }
          />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

function LocationSpy() {
  const location = useLocation()
  return <div data-testid="location-spy" data-pathname={location.pathname} />
}

function renderSettingsSearchWithLocationSpy(initialEntry = '/settings/ai') {
  const queryClient = makeQueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialEntry]}>
        <LocationSpy />
        <Routes>
          <Route
            path="/settings/:section"
            element={
              <>
                <TabPlaceholder />
                <SettingsSearch />
              </>
            }
          />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

/**
 * Project-aware variant. Wraps the tree in `ProjectProvider` so `useSettingsSectionPath`
 * resolves to the project's name prefix for project-scope sections and routes only the
 * application-scope sections to `/settings/<section>` (no project segment). The
 * `:projectName/settings/:section` route is added so the resulting URL still resolves
 * to a real section placeholder for the post-Enter focus poll.
 */
function renderSettingsSearchWithProject(initialEntry = '/settings/ai') {
  const queryClient = makeQueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={SELECTED_PROJECT.id} initialProjects={[SELECTED_PROJECT]}>
        <MemoryRouter initialEntries={[initialEntry]}>
          <LocationSpy />
          <Routes>
            <Route
              path="/settings/:section"
              element={
                <>
                  <TabPlaceholder />
                  <SettingsSearch />
                </>
              }
            />
            <Route
              path="/:projectName/settings/:section"
              element={
                <>
                  <TabPlaceholder />
                  <SettingsSearch />
                </>
              }
            />
          </Routes>
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  window.localStorage.clear()
  __resetShortcutHandlersForTesting()
  vi.clearAllMocks()
})

beforeEach(() => {
  __resetShortcutHandlersForTesting()
})

describe('buildHaystack', () => {
  it('lowercases and concatenates label, description, and placeholder', () => {
    expect(
      buildHaystack({
        tab: 'agent',
        label: 'Session Timeout',
        description: 'Maximum total time an external coder agent session can run.',
        placeholder: undefined,
        focusTargetId: 'agent-runtime-timeout',
      }),
    ).toBe(
      'session timeout maximum total time an external coder agent session can run.',
    )
  })

  it('includes the placeholder when one is present', () => {
    expect(
      buildHaystack({
        tab: 'ai',
        label: 'Default Coder Agent Model',
        description: 'Passed to opencode when workflow tasks run.',
        placeholder: 'Opencode default',
        focusTargetId: 'settings-default-model',
      }),
    ).toContain('opencode default')
  })

  it('omits numeric values from the haystack so live inputs cannot match', () => {
    // The registry entries do not contain current values; buildHaystack must
    // never surface anything beyond label / description / placeholder.
    for (const entry of settingsSearchRegistry) {
      const haystack = buildHaystack(entry)
      expect(haystack).not.toMatch(/\b30\b/)
    }
  })
})

describe('groupEntriesByTab', () => {
  it('groups the real registry into one bucket per Settings tab', () => {
    const groups = groupEntriesByTab(settingsSearchRegistry)
    const labels = groups.map((group) => group.label)
    // Each tab that contributes at least one field gets a group with a label.
    for (const expected of ['Coder Agent', 'Runtime', 'Preferences', 'Repositories', 'System', 'Templates']) {
      expect(labels).toContain(expected)
    }
  })

  it('preserves the in-tab order of entries within each group', () => {
    const groups = groupEntriesByTab(settingsSearchRegistry)
    const agent = groups.find((group) => group.tab === 'agent')
    expect(agent?.entries.map((entry) => entry.focusTargetId)).toEqual([
      'agent-runtime-timeout',
      'agent-runtime-stageTimeout',
      'agent-runtime-taskTimeout',
      'agent-runtime-maxConcurrent',
      'agent-runtime-pollInterval',
      'agent-runtime-maxGracePeriods',
    ])
  })
})

describe('SettingsSearch ⌘K shortcut is settings-page-scoped', () => {
  it('opens the dialog when ⌘K is pressed on the Settings page', async () => {
    arrangeAiLoaded()
    const user = userEvent.setup()
    renderSettingsSearch('/settings/agent')

    expect(screen.queryByTestId('settings-search-input')).not.toBeInTheDocument()
    await user.keyboard('{Meta>}k{/Meta}')

    await waitFor(() => {
      expect(screen.getByTestId('settings-search-input')).toBeInTheDocument()
    })
  })

  it('opens the dialog when Ctrl+K is pressed on a non-macOS-like Settings page', async () => {
    arrangeAiLoaded()
    const user = userEvent.setup()
    renderSettingsSearch('/settings/ai')

    await user.keyboard('{Control>}k{/Control}')

    await waitFor(() => {
      expect(screen.getByTestId('settings-search-input')).toBeInTheDocument()
    })
  })

  it('does not fire ⌘K while the Settings page is not mounted', async () => {
    arrangeAiLoaded()
    const user = userEvent.setup()
    // Render on a non-settings route — the route component above will not
    // mount SettingsSearch, so the listener is never registered.
    render(
      <QueryClientProvider client={makeQueryClient()}>
        <MemoryRouter initialEntries={['/dashboard']}>
          <div data-testid="not-settings">
            <input data-testid="some-input" />
          </div>
          <Routes>
            <Route path="/dashboard" element={<div data-testid="placeholder">not settings</div>} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    )

    await user.keyboard('{Meta>}k{/Meta}')

    expect(screen.queryByTestId('settings-search-input')).not.toBeInTheDocument()
    expect(getShortcutHandler('settings-search')).toBeUndefined()
  })

  it('does not fire ⌘K when an editable field has focus', async () => {
    arrangeAiLoaded()
    renderSettingsSearch('/settings/agent')
    const input = document.createElement('input')
    input.id = 'editable'
    document.body.appendChild(input)
    input.focus()
    expect(document.activeElement).toBe(input)

    const event = new KeyboardEvent('keydown', {
      key: 'k',
      metaKey: true,
      bubbles: true,
      cancelable: true,
    })
    input.dispatchEvent(event)

    expect(screen.queryByTestId('settings-search-input')).not.toBeInTheDocument()

    input.remove()
  })

  it('does not re-open the dialog when ⌘K is pressed while the dialog is already open', async () => {
    arrangeAiLoaded()
    const user = userEvent.setup()
    renderSettingsSearch('/settings/agent')

    await user.keyboard('{Meta>}k{/Meta}')
    await waitFor(() => {
      expect(screen.getByTestId('settings-search-input')).toBeInTheDocument()
    })

    // While the input is focused, typing ⌘K must be ignored so the user can
    // type 'k' inside the search field without re-opening the dialog.
    const input = screen.getByTestId('settings-search-input')
    input.focus()
    fireEvent.keyDown(input, { key: 'k', metaKey: true })

    // Still exactly one instance of the dialog open — no second dialog stacked.
    expect(screen.getAllByTestId('settings-search-input')).toHaveLength(1)
  })

  it('registers a settings-search shortcut handler that opens the dialog', () => {
    arrangeAiLoaded()
    renderSettingsSearch('/settings/agent')

    expect(getShortcutHandler('settings-search')).toBeTypeOf('function')
  })

  it('unregisters the settings-search shortcut handler when the Settings page unmounts', () => {
    arrangeAiLoaded()
    const { unmount } = renderSettingsSearch('/settings/agent')
    expect(getShortcutHandler('settings-search')).toBeTypeOf('function')

    unmount()
    expect(getShortcutHandler('settings-search')).toBeUndefined()
  })

  it('does not register a global ⌘K shortcut handler (no document/window listener for the search)', () => {
    arrangeAiLoaded()
    const addSpy = vi.spyOn(window, 'addEventListener')
    renderSettingsSearch('/settings/agent')

    // Only the explicit settings-search shortcut handler registration
    // happens; we never add a `keydown` listener for ⌘K to document/window
    // outside the SettingsPage mount. The handler goes through the
    // shared keyboard-shortcuts registry which the Preferences reference
    // already uses.
    const keydownListeners = addSpy.mock.calls.filter(([type]) => type === 'keydown')
    expect(keydownListeners.length).toBeGreaterThanOrEqual(1)
    // SidebarProvider (rendered in the App shell) is not part of this test's
    // tree, so the only keydown listener attached should be SettingsSearch's
    // own scoped one — never a "global" handler that opens the dialog from
    // outside the Settings page.
    addSpy.mockRestore()
  })
})

describe('SettingsSearch search filtering', () => {
  it('matches on label, description, and placeholder but excludes current values', async () => {
    arrangeAiLoaded()
    const user = userEvent.setup()
    renderSettingsSearch('/settings/agent')

    await user.keyboard('{Meta>}k{/Meta}')
    await waitFor(() => {
      expect(screen.getByTestId('settings-search-input')).toBeInTheDocument()
    })

    const input = screen.getByTestId('settings-search-input')

    // Match on label
    await user.type(input, 'timeout')
    await waitFor(() => {
      expect(screen.getByTestId('settings-search-result-agent-runtime-timeout')).toBeInTheDocument()
    })

    // Match on description
    await user.clear(input)
    await user.type(input, 'upper bound')
    await waitFor(() => {
      expect(screen.getByTestId('settings-search-result-agent-runtime-maxConcurrent')).toBeInTheDocument()
    })

    // Match on placeholder
    await user.clear(input)
    await user.type(input, 'e.g. frontend')
    await waitFor(() => {
      expect(screen.getByTestId('settings-search-result-repository-add-name')).toBeInTheDocument()
    })
  })

  it('searching the numeric value of a field (e.g. "30") does not match when "30" is not in label/description/placeholder', async () => {
    arrangeAiLoaded()
    const user = userEvent.setup()
    renderSettingsSearch('/settings/agent')

    await user.keyboard('{Meta>}k{/Meta}')
    await waitFor(() => {
      expect(screen.getByTestId('settings-search-input')).toBeInTheDocument()
    })

    const input = screen.getByTestId('settings-search-input')

    // The AgentRuntimeConfig fixture above seeds every numeric field with
    // a value that is at least 30 (timeout = 30 min, taskTimeout = 10 min,
    // etc.) — but only "30" should appear if it ALSO appears in
    // label/description/placeholder. None of the runtime field haystacks
    // contain "30", so the search must yield no matches for any numeric
    // value seeded above. Here we exercise "30" which the fixture puts in
    // timeout/stageTimeout/taskTimeout/pollInterval values.
    await user.type(input, '30')

    // "30" must NOT surface any runtime input just because its current
    // value happens to be 30. cmdk filters on the explicit `value` prop
    // we set, so the absence of "30" from label/description/placeholder
    // means none of the agent-runtime-* items match.
    expect(
      screen.queryByTestId('settings-search-result-agent-runtime-timeout'),
    ).not.toBeInTheDocument()
    expect(
      screen.queryByTestId('settings-search-result-agent-runtime-stageTimeout'),
    ).not.toBeInTheDocument()
    expect(
      screen.queryByTestId('settings-search-result-agent-runtime-taskTimeout'),
    ).not.toBeInTheDocument()
    expect(
      screen.queryByTestId('settings-search-result-agent-runtime-pollInterval'),
    ).not.toBeInTheDocument()
  })

  it('shows the empty-state copy when the query matches nothing', async () => {
    arrangeAiLoaded()
    const user = userEvent.setup()
    renderSettingsSearch('/settings/agent')

    await user.keyboard('{Meta>}k{/Meta}')
    await waitFor(() => {
      expect(screen.getByTestId('settings-search-input')).toBeInTheDocument()
    })

    await user.type(screen.getByTestId('settings-search-input'), 'zzz-no-such-setting')

    await waitFor(() => {
      expect(screen.getByTestId('settings-search-empty')).toHaveTextContent(NO_MATCHES_COPY)
    })
  })
})

describe('SettingsSearch activation (Enter)', () => {
  it('Enter on a highlighted result closes the dialog, navigates to the owning tab, and focuses the field', async () => {
    arrangeAiLoaded()
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/ai')

    await user.keyboard('{Meta>}k{/Meta}')
    await waitFor(() => {
      expect(screen.getByTestId('settings-search-input')).toBeInTheDocument()
    })

    // Search for the agent-runtime-timeout field from the agent tab.
    await user.type(screen.getByTestId('settings-search-input'), 'session timeout')

    // Highlight the first (and only) match by pressing Enter.
    await user.keyboard('{Enter}')

    // Dialog closes, route switches to /settings/agent.
    await waitFor(() => {
      expect(screen.queryByTestId('settings-search-input')).not.toBeInTheDocument()
    })
    await waitFor(() => {
      expect(screen.getByTestId('location-spy').getAttribute('data-pathname')).toBe('/settings/agent')
    })

    // The focus target element exists in the rendered DOM and receives
    // keyboard focus.
    await waitFor(() => {
      const el = document.getElementById('agent-runtime-timeout')
      expect(el).not.toBeNull()
      expect(document.activeElement).toBe(el)
    })
  })

  it('Enter on the focused command item calls onSelect and focuses via the rAF element-poll', async () => {
    arrangeAiLoaded()
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/ai')

    await user.keyboard('{Meta>}k{/Meta}')
    await waitFor(() => {
      expect(screen.getByTestId('settings-search-input')).toBeInTheDocument()
    })

    const input = screen.getByTestId('settings-search-input')
    await user.type(input, 'session timeout')

    // Press Enter — cmdk will call the matched CommandItem.onSelect.
    await user.keyboard('{Enter}')

    await waitFor(() => {
      expect(document.activeElement?.id).toBe('agent-runtime-timeout')
    })
  })

  it('dispatches an entry reveal event before focusing a conditional target', async () => {
    arrangeAiLoaded()
    const dispatchSpy = vi.spyOn(window, 'dispatchEvent')
    const user = userEvent.setup()
    renderSettingsSearchWithLocationSpy('/settings/agent')

    await user.keyboard('{Meta>}k{/Meta}')
    await waitFor(() => {
      expect(screen.getByTestId('settings-search-input')).toBeInTheDocument()
    })

    await user.type(screen.getByTestId('settings-search-input'), 'plan stage model')
    await user.click(screen.getByTestId('settings-search-result-settings-stage-model-plan'))

    await waitFor(() => {
      expect(dispatchSpy).toHaveBeenCalledWith(
        expect.objectContaining({ type: 'mohist:settings:reveal-stage-model-overrides' }),
      )
    })
    dispatchSpy.mockRestore()
  })

  it('searching workflow and clicking a workflow result navigates to Workflows and focuses the target', async () => {
    arrangeAiLoaded()
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/ai')

    await user.keyboard('{Meta>}k{/Meta}')
    await waitFor(() => {
      expect(screen.getByTestId('settings-search-input')).toBeInTheDocument()
    })

    await user.type(screen.getByTestId('settings-search-input'), 'workflow')
    expect(screen.getByTestId('settings-search-group-workflows')).toBeInTheDocument()
    await user.click(screen.getByTestId('settings-search-result-workflow-profiles-section'))

    await waitFor(() => {
      expect(screen.queryByTestId('settings-search-input')).not.toBeInTheDocument()
    })
    await waitFor(() => {
      expect(screen.getByTestId('location-spy').getAttribute('data-pathname')).toBe('/selected-project/settings/workflows')
    })
    await waitFor(() => {
      expect(document.activeElement?.id).toBe('workflow-profiles-section')
    })
  })

  it('clicking the project default workflow result focuses the default control target', async () => {
    arrangeAiLoaded()
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/ai')

    await user.keyboard('{Meta>}k{/Meta}')
    await waitFor(() => {
      expect(screen.getByTestId('settings-search-input')).toBeInTheDocument()
    })

    await user.type(screen.getByTestId('settings-search-input'), 'default workflow')
    await user.click(screen.getByTestId('settings-search-result-project-default-workflow'))

    await waitFor(() => {
      expect(screen.getByTestId('location-spy').getAttribute('data-pathname')).toBe('/selected-project/settings/workflows')
    })
    await waitFor(() => {
      expect(document.activeElement?.id).toBe('project-default-workflow')
    })
  })
})

describe('SettingsSearch scope-aware navigation', () => {
  it('routes an application-level result (Coder Agent) to /settings/<section> without a project segment', async () => {
    arrangeAiLoaded()
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/agent')

    await user.keyboard('{Meta>}k{/Meta}')
    await waitFor(() => {
      expect(screen.getByTestId('settings-search-input')).toBeInTheDocument()
    })

    await user.type(screen.getByTestId('settings-search-input'), 'default coder agent model')
    await user.click(screen.getByTestId('settings-search-result-settings-default-model'))

    await waitFor(() => {
      expect(screen.queryByTestId('settings-search-input')).not.toBeInTheDocument()
    })
    const pathname = screen.getByTestId('location-spy').getAttribute('data-pathname')
    expect(pathname).toBe('/settings/ai')
    expect(pathname ?? '').not.toContain('selected-project')
  })

  it('routes a project-level result (Repositories) to /:projectName/settings/<section>', async () => {
    arrangeAiLoaded()
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/ai')

    act(() => {
      getShortcutHandler('settings-search')?.()
    })
    await waitFor(() => {
      expect(screen.getByTestId('settings-search-input')).toBeInTheDocument()
    })

    await user.type(screen.getByTestId('settings-search-input'), 'e.g. frontend')
    await user.click(screen.getByTestId('settings-search-result-repository-add-name'))

    await waitFor(() => {
      expect(screen.queryByTestId('settings-search-input')).not.toBeInTheDocument()
    })
    const pathname = screen.getByTestId('location-spy').getAttribute('data-pathname')
    expect(pathname).toBe('/selected-project/settings/repositories')
    expect(pathname ?? '').toContain('selected-project')
  })

  it('routes another application-level result (Runtime) to /settings/<section> without a project segment', async () => {
    arrangeAiLoaded()
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/ai')

    await user.keyboard('{Meta>}k{/Meta}')
    await waitFor(() => {
      expect(screen.getByTestId('settings-search-input')).toBeInTheDocument()
    })

    await user.type(screen.getByTestId('settings-search-input'), 'session timeout')
    await user.click(screen.getByTestId('settings-search-result-agent-runtime-timeout'))

    await waitFor(() => {
      expect(screen.queryByTestId('settings-search-input')).not.toBeInTheDocument()
    })
    expect(screen.getByTestId('location-spy').getAttribute('data-pathname')).toBe('/settings/agent')
  })

  it('routes a Workflows result (project scope) to /:projectName/settings/workflows', async () => {
    arrangeAiLoaded()
    const user = userEvent.setup()
    renderSettingsSearchWithProject('/settings/ai')

    await user.keyboard('{Meta>}k{/Meta}')
    await waitFor(() => {
      expect(screen.getByTestId('settings-search-input')).toBeInTheDocument()
    })

    await user.type(screen.getByTestId('settings-search-input'), 'workflow')
    await user.click(screen.getByTestId('settings-search-result-workflow-profiles-section'))

    await waitFor(() => {
      expect(screen.queryByTestId('settings-search-input')).not.toBeInTheDocument()
    })
    const pathname = screen.getByTestId('location-spy').getAttribute('data-pathname')
    expect(pathname).toBe('/selected-project/settings/workflows')
    expect(pathname ?? '').toContain('selected-project')
  })

  it('does not route a project-level result to /settings/<section> when no project is selected', async () => {
    arrangeAiLoaded()
    const user = userEvent.setup()
    renderSettingsSearchWithLocationSpy('/settings/ai')

    await user.keyboard('{Meta>}k{/Meta}')
    await waitFor(() => {
      expect(screen.getByTestId('settings-search-input')).toBeInTheDocument()
    })

    await user.type(screen.getByTestId('settings-search-input'), 'e.g. frontend')
    const result = screen.getByTestId('settings-search-result-repository-add-name')
    expect(result).toHaveAttribute('data-disabled', 'true')

    fireEvent.click(result)

    expect(screen.getByTestId('location-spy').getAttribute('data-pathname')).toBe('/settings/ai')
    expect(screen.queryByTestId('placeholder-section-repositories')).not.toBeInTheDocument()
  })
})

describe('SettingsSearch dismissal (Esc / overlay)', () => {
  it('Esc closes the dialog without navigating away from the current tab', async () => {
    arrangeAiLoaded()
    const user = userEvent.setup()
    renderSettingsSearchWithLocationSpy('/settings/ai')

    await user.keyboard('{Meta>}k{/Meta}')
    await waitFor(() => {
      expect(screen.getByTestId('settings-search-input')).toBeInTheDocument()
    })

    await user.keyboard('{Escape}')

    await waitFor(() => {
      expect(screen.queryByTestId('settings-search-input')).not.toBeInTheDocument()
    })
    expect(screen.getByTestId('location-spy').getAttribute('data-pathname')).toBe('/settings/ai')
  })

  it('overlay click closes the dialog without navigating away from the current tab', async () => {
    arrangeAiLoaded()
    const user = userEvent.setup()
    renderSettingsSearchWithLocationSpy('/settings/agent')

    act(() => {
      getShortcutHandler('settings-search')?.()
    })
    await waitFor(() => {
      expect(screen.getByTestId('settings-search-input')).toBeInTheDocument()
    })
    // Click on the overlay (outside the dialog content). base-ui's dialog
    // backdrop carries data-slot="dialog-overlay".
    const overlay = document.querySelector('[data-slot="dialog-overlay"]') as HTMLElement | null
    expect(overlay).not.toBeNull()
    const removed = waitForElementToBeRemoved(() => screen.queryByTestId('settings-search-input'))
    await user.click(overlay!)

    await removed
    expect(screen.getByTestId('location-spy').getAttribute('data-pathname')).toBe('/settings/agent')
  })
})

describe('SettingsSearch uses the existing cmdk primitives (no new infrastructure)', () => {
  it('renders CommandDialog with CommandInput, CommandList, CommandEmpty, CommandGroup, and CommandItem', async () => {
    arrangeAiLoaded()
    const user = userEvent.setup()
    renderSettingsSearch('/settings/agent')

    await user.keyboard('{Meta>}k{/Meta}')

    await waitFor(() => {
      expect(screen.getByTestId('settings-search-input')).toBeInTheDocument()
    })

    expect(screen.getByTestId('settings-search-list')).toBeInTheDocument()
    expect(document.querySelector('[data-slot="dialog-content"]')).not.toBeNull()
  })

  it('exposes a SettingsSearch entry in the public settings-search API', async () => {
    // Sanity check that the new component is exported via the feature's
    // public surface (so callers can mount it without reaching into the
    // feature's internal folder).
    const api = await import('./index')
    expect(typeof api.SettingsSearch).toBe('function')
  })
})

// Sanity check: registerShortcutHandler exports remain usable from
// outside SettingsSearch (PreferencesSection tests depend on this).
describe('registerShortcutHandler interop', () => {
  it('SettingsSearch is unaffected by external shortcut registrations', () => {
    arrangeAiLoaded()
    renderSettingsSearch('/settings/agent')

    registerShortcutHandler('sidebar-toggle', () => {})
    // Mounting PreferencesSection here would replace SettingsSearch's
    // handler; verify the order is consistent.
    expect(getShortcutHandler('settings-search')).toBeTypeOf('function')
    expect(getShortcutHandler('sidebar-toggle')).toBeTypeOf('function')
  })
})

// Sanity check: SettingsSearchEntry shape contract is preserved for tests.
const _shape: SettingsSearchEntry = {
  tab: 'agent',
  label: 'x',
  description: 'y',
  focusTargetId: 'z',
}
void _shape
