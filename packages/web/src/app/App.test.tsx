// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render } from '@testing-library/react'
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
})
