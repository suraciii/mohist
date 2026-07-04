// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { LogEntry } from '../model/api'
import type { UseLogsReturn } from '../model/useLogs'

const useLogsMock = vi.fn<() => UseLogsReturn>()

vi.mock('../model/useLogs', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../model/useLogs')>()
  return {
    ...actual,
    useLogs: useLogsMock,
  }
})

const { LogsPage } = await import('./LogsPage')

function makeEntry(overrides: Partial<LogEntry> = {}): LogEntry {
  return {
    level: 'INFO',
    time: '2026-07-04T08:00:00.000Z',
    service: 'Mohist.Server',
    message: 'hello',
    raw: '{"level":"INFO","message":"hello"}',
    ...overrides,
  }
}

function defaultUseLogs(overrides: Partial<UseLogsReturn> = {}): UseLogsReturn {
  return {
    entries: [],
    loading: false,
    error: null,
    refresh: vi.fn(),
    cursor: null,
    nextCursor: null,
    source: null,
    unavailable: false,
    expectedLocation: null,
    reason: null,
    truncated: false,
    reset: false,
    ...overrides,
  }
}

beforeEach(() => {
  useLogsMock.mockReset()
  useLogsMock.mockImplementation(() => defaultUseLogs())
})

afterEach(() => {
  cleanup()
  vi.useRealTimers()
})

describe('LogsPage: File: source line', () => {
  it('renders the File: line using the real source identity returned by the API', () => {
    useLogsMock.mockImplementation(() =>
      defaultUseLogs({
        source: 'server.log',
        entries: [makeEntry({ message: 'started', raw: 'started' })],
      }),
    )

    render(<LogsPage />)

    expect(screen.getByText(/^File:/)).toBeInTheDocument()
    expect(screen.getByText(/File: server\.log/)).toBeInTheDocument()
  })

  it('does not render a File: line when the source identity is missing', () => {
    useLogsMock.mockImplementation(() => defaultUseLogs({ entries: [] }))

    render(<LogsPage />)

    expect(screen.queryByText(/^File:/)).not.toBeInTheDocument()
  })
})

describe('LogsPage: unavailable diagnostic', () => {
  it('renders an actionable diagnostic with expected location and reason when unavailable', () => {
    useLogsMock.mockImplementation(() =>
      defaultUseLogs({
        unavailable: true,
        expectedLocation: '/home/me/.mohist/logs/server.log',
        reason: 'Log directory does not exist at /home/me/.mohist/logs.',
        source: null,
        entries: [],
      }),
    )

    render(<LogsPage />)

    const diagnostic = screen.getByTestId('logs-unavailable')
    expect(diagnostic).toBeInTheDocument()
    expect(diagnostic).toHaveTextContent('/home/me/.mohist/logs/server.log')
    expect(diagnostic).toHaveTextContent('Log directory does not exist at /home/me/.mohist/logs.')

    // The bare "No logs available" copy must NOT appear when unavailable.
    expect(screen.queryByText('No logs available')).not.toBeInTheDocument()
  })

  it('does NOT render the unavailable diagnostic when the source is available but the view is empty', () => {
    useLogsMock.mockImplementation(() => defaultUseLogs({ entries: [] }))

    render(<LogsPage />)

    expect(screen.queryByTestId('logs-unavailable')).not.toBeInTheDocument()
  })

  it('does NOT render the unavailable diagnostic when filtering hides every entry', async () => {
    useLogsMock.mockImplementation(() =>
      defaultUseLogs({
        source: 'server.log',
        entries: [makeEntry({ level: 'INFO', message: 'matched', raw: 'matched' })],
      }),
    )

    render(<LogsPage />)

    // Disable every level chip so the only entry is filtered out.
    const user = userEvent.setup()
    for (const level of ['DEBUG', 'INFO', 'WARN', 'ERROR']) {
      await user.click(screen.getByRole('button', { name: level }))
    }

    await waitFor(() => {
      expect(screen.getByText('No matching logs')).toBeInTheDocument()
    })

    expect(screen.queryByTestId('logs-unavailable')).not.toBeInTheDocument()
  })

  it('does NOT render the bare "No logs available" empty state in any branch', () => {
    const branches: Array<Partial<UseLogsReturn>> = [
      { entries: [] },
      { entries: [], unavailable: true, expectedLocation: '/x', reason: 'r' },
      {
        source: 'server.log',
        entries: [makeEntry({ level: 'INFO', message: 'm', raw: 'm' })],
      },
    ]

    for (const branch of branches) {
      useLogsMock.mockImplementation(() => defaultUseLogs(branch))
      cleanup()
      render(<LogsPage />)
      expect(screen.queryByText('No logs available')).not.toBeInTheDocument()
    }
  })
})

describe('LogsPage: level filtering and search operate on the agreed element type', () => {
  it('level filter hides entries whose level is disabled', () => {
    useLogsMock.mockImplementation(() =>
      defaultUseLogs({
        source: 'server.log',
        entries: [
          makeEntry({ level: 'INFO', message: 'info-msg', raw: 'info-msg' }),
          makeEntry({ level: 'ERROR', message: 'err-msg', raw: 'err-msg' }),
        ],
      }),
    )

    render(<LogsPage />)

    expect(screen.getByText('info-msg')).toBeInTheDocument()
    expect(screen.getByText('err-msg')).toBeInTheDocument()

    // Disable INFO so only ERROR remains visible.
    act(() => {
      screen.getByRole('button', { name: 'INFO' }).click()
    })

    expect(screen.queryByText('info-msg')).not.toBeInTheDocument()
    expect(screen.getByText('err-msg')).toBeInTheDocument()
  })

  it('search filters across message, service, and raw', async () => {
    useLogsMock.mockImplementation(() =>
      defaultUseLogs({
        source: 'server.log',
        entries: [
          makeEntry({ level: 'INFO', service: 'Mohist.Workflow', message: 'alpha', raw: '{"level":"INFO","message":"alpha"}' }),
          makeEntry({ level: 'INFO', service: 'Mohist.Server', message: 'beta', raw: '{"level":"INFO","message":"beta"}' }),
          makeEntry({ level: 'INFO', service: 'Mohist.Server', message: 'gamma', raw: '{"level":"INFO","message":"gamma"}' }),
        ],
      }),
    )

    const user = userEvent.setup()
    render(<LogsPage />)

    const input = screen.getByPlaceholderText('Search logs...')
    await user.type(input, 'beta')

    await waitFor(() => {
      expect(screen.getByText('beta')).toBeInTheDocument()
      expect(screen.queryByText('alpha')).not.toBeInTheDocument()
      expect(screen.queryByText('gamma')).not.toBeInTheDocument()
    })
  })
})

describe('LogsPage: loading state', () => {
  it('renders the loading placeholder while the first fetch is in flight', () => {
    useLogsMock.mockImplementation(() => defaultUseLogs({ loading: true, entries: [] }))

    render(<LogsPage />)

    expect(screen.getByText('Loading logs...')).toBeInTheDocument()
  })
})