// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import App from './App'

const projectMocks = vi.hoisted(() => ({
  useProjects: vi.fn(),
}))

const epicMocks = vi.hoisted(() => ({
  useEpic: vi.fn(),
}))

const eventMocks = vi.hoisted(() => ({
  useEventsConnection: vi.fn(),
}))

const inboxMocks = vi.hoisted(() => ({
  useInbox: vi.fn(),
  useMarkInboxItemRead: vi.fn(),
  useMarkAllInboxRead: vi.fn(),
  useArchiveInboxItem: vi.fn(),
}))

const logsMocks = vi.hoisted(() => ({
  useLogs: vi.fn(),
}))

const TEST_PROJECT = {
  id: 'test-project',
  name: 'demo',
  createdAt: '2024-01-01T00:00:00.000Z',
  updatedAt: '2024-01-01T00:00:00.000Z',
  repositories: [],
}

vi.mock('../entities/project', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../entities/project')>()
  return {
    ...actual,
    useProjects: projectMocks.useProjects,
  }
})

vi.mock('../entities/agent', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../entities/agent')>()
  return {
    ...actual,
    useAgentStatus: () => ({
      data: { running: false, activeAgents: [], capacity: { active: 0, max: 8 } },
    }),
  }
})

vi.mock('../entities/epic', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../entities/epic')>()
  return {
    ...actual,
    useEpic: epicMocks.useEpic,
  }
})

vi.mock('../shared/api/events-hub', () => ({
  useEventsConnection: (...args: unknown[]) => eventMocks.useEventsConnection(...args),
}))

vi.mock('../entities/inbox', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../entities/inbox')>()
  return {
    ...actual,
    useInbox: inboxMocks.useInbox,
    useMarkInboxItemRead: inboxMocks.useMarkInboxItemRead,
    useMarkAllInboxRead: inboxMocks.useMarkAllInboxRead,
    useArchiveInboxItem: inboxMocks.useArchiveInboxItem,
  }
})

vi.mock('../pages/logs/model/useLogs', () => ({
  useLogs: logsMocks.useLogs,
}))

function renderApp() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <App />
    </QueryClientProvider>,
  )
}

function getShellContentContainer(): HTMLElement | null {
  return document.querySelector<HTMLElement>(
    '[data-slot="sidebar-inset"] > .flex-1.min-h-0.flex.flex-col',
  )
}

describe('App shell bottom spacing for mobile bottom nav', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/demo')
    projectMocks.useProjects.mockReturnValue({
      data: [TEST_PROJECT],
      isLoading: false,
    })
    epicMocks.useEpic.mockReturnValue({ data: undefined, isLoading: false })
    eventMocks.useEventsConnection.mockReturnValue('disconnected')
    inboxMocks.useInbox.mockReturnValue({ data: [], isLoading: false })
    inboxMocks.useMarkInboxItemRead.mockReturnValue({ mutate: vi.fn(), isPending: false })
    inboxMocks.useMarkAllInboxRead.mockReturnValue({ mutate: vi.fn(), isPending: false })
    inboxMocks.useArchiveInboxItem.mockReturnValue({ mutate: vi.fn(), isPending: false })
    logsMocks.useLogs.mockReturnValue({
      entries: [],
      loading: false,
      error: null,
      refresh: vi.fn(),
      cursor: 0,
      truncated: false,
      file: null,
    })
  })

  afterEach(() => {
    cleanup()
    window.history.replaceState({}, '', '/')
    window.localStorage.clear()
  })

  it('uses a calc-based bottom padding that adds safe-area-inset-bottom on mobile', () => {
    renderApp()

    const shellContainer = getShellContentContainer()
    expect(shellContainer).not.toBeNull()

    const classNames = shellContainer!.className.split(/\s+/)
    expect(classNames).toContain('pb-[calc(3.5rem+env(safe-area-inset-bottom))]')
    expect(classNames).toContain('md:pb-0')
  })

  it('does not use the legacy pb-14 fixed padding', () => {
    renderApp()

    const shellContainer = getShellContentContainer()
    expect(shellContainer).not.toBeNull()

    const classNames = shellContainer!.className.split(/\s+/)
    expect(classNames).not.toContain('pb-14')
  })

  it('routes /:projectName/inbox to the project inbox page', () => {
    window.history.replaceState({}, '', '/demo/inbox')

    const { getByTestId } = renderApp()

    expect(getByTestId('inbox-title')).toHaveTextContent('Inbox')
    expect(inboxMocks.useInbox).toHaveBeenCalled()
  })
})

describe('App routing split for settings scopes', () => {
  beforeEach(() => {
    projectMocks.useProjects.mockReturnValue({
      data: [TEST_PROJECT],
      isLoading: false,
    })
    epicMocks.useEpic.mockReturnValue({ data: undefined, isLoading: false })
    eventMocks.useEventsConnection.mockReturnValue('disconnected')
    inboxMocks.useInbox.mockReturnValue({ data: [], isLoading: false })
    inboxMocks.useMarkInboxItemRead.mockReturnValue({ mutate: vi.fn(), isPending: false })
    inboxMocks.useMarkAllInboxRead.mockReturnValue({ mutate: vi.fn(), isPending: false })
    inboxMocks.useArchiveInboxItem.mockReturnValue({ mutate: vi.fn(), isPending: false })
    logsMocks.useLogs.mockReturnValue({
      entries: [],
      loading: false,
      error: null,
      refresh: vi.fn(),
      cursor: 0,
      truncated: false,
      file: null,
    })
  })

  afterEach(() => {
    cleanup()
    window.history.replaceState({}, '', '/')
    window.localStorage.clear()
  })

  it('redirects legacy /:projectName/settings/ai (global section under project scope) to /settings/ai', () => {
    window.history.replaceState({}, '', '/demo/settings/ai')

    renderApp()

    // After the replace navigation, the URL must be the application-scope URL.
    expect(window.location.pathname).toBe('/settings/ai')
  })

  it('renders /settings/ai without showing the "No projects yet" gate even when projects exist', () => {
    window.history.replaceState({}, '', '/settings/ai')

    const { queryByText } = renderApp()

    expect(queryByText('No projects yet')).not.toBeInTheDocument()
  })

  it('does not redirect project-scoped /:projectName/settings/repositories', () => {
    window.history.replaceState({}, '', '/demo/settings/repositories')

    renderApp()

    // The project-scoped repositories URL must NOT be redirected to the
    // application scope (D2 risk: project URLs must stay project-scoped).
    expect(window.location.pathname).toBe('/demo/settings/repositories')
  })

  it('redirects legacy /:projectName/settings/agent, /system, /preferences to the application scope', () => {
    for (const section of ['agent', 'system', 'preferences']) {
      window.history.replaceState({}, '', `/demo/settings/${section}`)
      renderApp()
      expect(window.location.pathname).toBe(`/settings/${section}`)
      cleanup()
    }
  })

  it('does not redirect /:projectName/settings/<project-section> for any project section', () => {
    for (const section of ['repositories', 'templates', 'label-catalog', 'workflows', 'inbox']) {
      window.history.replaceState({}, '', `/demo/settings/${section}`)
      renderApp()
      expect(window.location.pathname).toBe(`/demo/settings/${section}`)
      cleanup()
    }
  })

  it('keeps application settings reachable when no project exists (no "No projects yet" prompt)', () => {
    projectMocks.useProjects.mockReturnValue({ data: [], isLoading: false })
    window.history.replaceState({}, '', '/settings/ai')

    const { queryByText } = renderApp()

    expect(queryByText('No projects yet')).not.toBeInTheDocument()
  })

  it('does not serve project settings from /settings/<project-section> when no project exists', () => {
    projectMocks.useProjects.mockReturnValue({ data: [], isLoading: false })
    window.history.replaceState({}, '', '/settings/repositories')

    const { queryByText, queryByTestId } = renderApp()

    expect(window.location.pathname).toBe('/settings/repositories')
    expect(queryByTestId('repositories-section')).not.toBeInTheDocument()
    expect(queryByText('No project selected')).not.toBeInTheDocument()
    expect(queryByTestId('no-project-select-button')).toBeInTheDocument()
    expect(queryByTestId('no-project-create-button')).toBeInTheDocument()
  })

  it('redirects /settings/<project-section> to the selected project scope once a project is available', async () => {
    window.history.replaceState({}, '', '/settings/repositories')

    renderApp()

    await waitFor(() => {
      expect(window.location.pathname).toBe('/demo/settings/repositories')
    })
  })

  it('clicking Logs from the sidebar renders the project-scoped Logs page', async () => {
    window.history.replaceState({}, '', '/demo')

    renderApp()

    await waitFor(() => {
      expect(screen.getByText('demo')).toBeInTheDocument()
    })
    fireEvent.click(screen.getByTestId('nav-logs'))

    await waitFor(() => {
      expect(window.location.pathname).toBe('/demo/logs')
    })
    expect(logsMocks.useLogs).toHaveBeenCalled()
    expect(screen.getByText('No logs available')).toBeInTheDocument()
  })

  it('renders /settings/<app-section> on the application scope (no project prefix) for every global section', () => {
    for (const section of ['ai', 'agent', 'system', 'preferences']) {
      window.history.replaceState({}, '', `/settings/${section}`)
      renderApp()
      expect(window.location.pathname).toBe(`/settings/${section}`)
      cleanup()
    }
  })
})
