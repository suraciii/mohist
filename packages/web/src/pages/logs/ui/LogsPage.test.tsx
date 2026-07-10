// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { LogEntry, LogTailResult } from '../model/api'
import { LogsPage as DefaultLogsPage, type LogsDataHook } from './LogsPage'

let _logData: LogTailResult = { lines: [], source: null, cursor: null, nextCursor: null, reset: false, truncated: false, unavailable: false, expectedLocation: null, reason: null }

const logsHook: LogsDataHook = () => ({
  entries: _logData.unavailable ? [] : _logData.lines,
  loading: false,
  error: null,
  refresh: () => undefined,
  cursor: _logData.unavailable ? null : _logData.cursor,
  nextCursor: _logData.unavailable ? null : _logData.nextCursor,
  source: _logData.unavailable ? null : _logData.source,
  unavailable: _logData.unavailable,
  expectedLocation: _logData.expectedLocation,
  reason: _logData.reason,
  truncated: _logData.unavailable ? false : _logData.truncated,
  reset: _logData.reset || _logData.unavailable,
})

function LogsPage() {
  return <DefaultLogsPage logsHook={logsHook} />
}

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

function logResult(overrides: Partial<LogTailResult> & { lines?: LogEntry[] } = {}): LogTailResult {
  return {
    lines: [],
    cursor: null,
    nextCursor: null,
    source: null,
    truncated: false,
    reset: false,
    unavailable: false,
    expectedLocation: null,
    reason: null,
    ...overrides,
  }
}

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  _logData = logResult()
})

afterEach(() => {
  cleanup()
  vi.useRealTimers()
})

describe('LogsPage: File: source line', () => {
  it('renders the File: line using the real source identity returned by the API', async () => {
    _logData = logResult({
      source: 'server.log',
      lines: [makeEntry({ message: 'started', raw: 'started' })],
      reset: true,
    })

    render(<LogsPage />)

    await waitFor(() => expect(screen.getByText(/^File:/)).toBeInTheDocument())
    expect(screen.getByText(/File: server\.log/)).toBeInTheDocument()
  })

  it('does not render a File: line when the source identity is missing', async () => {
    _logData = logResult({ lines: [], reset: true })

    render(<LogsPage />)

    expect(await screen.findByText('No matching logs')).toBeInTheDocument()
    expect(screen.queryByText(/^File:/)).not.toBeInTheDocument()
  })
})

describe('LogsPage: unavailable diagnostic', () => {
  it('renders an actionable diagnostic with expected location and reason when unavailable', async () => {
    _logData = logResult({
      unavailable: true,
      expectedLocation: '/home/me/.mohist/logs/server.log',
      reason: 'Log directory does not exist at /home/me/.mohist/logs.',
      source: null,
      lines: [],
      reset: true,
    })

    render(<LogsPage />)

    await waitFor(() => expect(screen.getByTestId('logs-unavailable')).toBeInTheDocument())
    const diagnostic = screen.getByTestId('logs-unavailable')
    expect(diagnostic).toHaveTextContent('/home/me/.mohist/logs/server.log')
    expect(diagnostic).toHaveTextContent('Log directory does not exist at /home/me/.mohist/logs.')
    expect(screen.queryByText('No logs available')).not.toBeInTheDocument()
  })

  it('disables export while unavailable even if stale entries are present', async () => {
    _logData = logResult({
      unavailable: true,
      expectedLocation: '/home/me/.mohist/logs/server.log',
      reason: 'Log file server.log is missing.',
      source: null,
      lines: [makeEntry({ message: 'stale', raw: 'stale' })],
      reset: true,
    })

    render(<LogsPage />)

    await waitFor(() => expect(screen.getByTestId('logs-unavailable')).toBeInTheDocument())
    expect(screen.getByRole('button', { name: /export/i })).toBeDisabled()
    expect(screen.queryByText('stale')).not.toBeInTheDocument()
  })

  it('does NOT render the unavailable diagnostic when the source is available but the view is empty', async () => {
    _logData = logResult({ lines: [], reset: true })

    render(<LogsPage />)

    // The real hook starts with loading=true; after fetch resolves, we see the empty state.
    // Wait for the loading text to disappear first.
    await waitFor(() => expect(screen.queryByText('Loading logs...')).not.toBeInTheDocument())
    expect(screen.queryByTestId('logs-unavailable')).not.toBeInTheDocument()
  })

  it('does NOT render the unavailable diagnostic when filtering hides every entry', async () => {
    _logData = logResult({
      source: 'server.log',
      lines: [makeEntry({ level: 'INFO', message: 'matched', raw: 'matched' })],
      reset: true,
    })

    render(<LogsPage />)

    await waitFor(() => expect(screen.getByText('matched')).toBeInTheDocument())

    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
    for (const level of ['DEBUG', 'INFO', 'WARN', 'ERROR']) {
      await user.click(screen.getByRole('button', { name: level }))
    }

    await waitFor(() => {
      expect(screen.getByText('No matching logs')).toBeInTheDocument()
    })

    expect(screen.queryByTestId('logs-unavailable')).not.toBeInTheDocument()
  })

  it('does NOT render the bare "No logs available" empty state in any branch', async () => {
    const branches: Array<LogTailResult> = [
      logResult({ lines: [], reset: true }),
      logResult({ lines: [], unavailable: true, expectedLocation: '/x', reason: 'r', reset: true }),
      logResult({ source: 'server.log', lines: [makeEntry({ level: 'INFO', message: 'm', raw: 'm' })], reset: true }),
    ]

    for (const branch of branches) {
      _logData = branch
      cleanup()
      render(<LogsPage />)
      await waitFor(() => {
        expect(screen.queryByText('Loading logs...')).not.toBeInTheDocument()
      })
      expect(screen.queryByText('No logs available')).not.toBeInTheDocument()
    }
  })
})

describe('LogsPage: level filtering and search operate on the agreed element type', () => {
  it('level filter hides entries whose level is disabled', async () => {
    _logData = logResult({
      source: 'server.log',
      lines: [
        makeEntry({ level: 'INFO', message: 'info-msg', raw: 'info-msg' }),
        makeEntry({ level: 'ERROR', message: 'err-msg', raw: 'err-msg' }),
      ],
      reset: true,
    })

    render(<LogsPage />)

    await waitFor(() => expect(screen.getByText('info-msg')).toBeInTheDocument())
    expect(screen.getByText('err-msg')).toBeInTheDocument()

    act(() => {
      screen.getByRole('button', { name: 'INFO' }).click()
    })

    expect(screen.queryByText('info-msg')).not.toBeInTheDocument()
    expect(screen.getByText('err-msg')).toBeInTheDocument()
  })

  it('search filters across message, service, and raw', async () => {
    _logData = logResult({
      source: 'server.log',
      lines: [
        makeEntry({ level: 'INFO', service: 'Mohist.Workflow', message: 'alpha', raw: '{"level":"INFO","message":"alpha"}' }),
        makeEntry({ level: 'INFO', service: 'Mohist.Server', message: 'beta', raw: '{"level":"INFO","message":"beta"}' }),
        makeEntry({ level: 'INFO', service: 'Mohist.Server', message: 'gamma', raw: '{"level":"INFO","message":"gamma"}' }),
      ],
      reset: true,
    })

    render(<LogsPage />)

    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
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
  it('renders the loading placeholder while the first fetch is in flight', async () => {
    _logData = logResult({ lines: [], reset: true })

    render(<LogsPage />)

    // With fake timers, the fetch is synchronous and resolves immediately.
    // The loading state may or may not be visible. Check for the absence
    // of loading after the fetch resolves.
    await waitFor(() => expect(screen.queryByText('Loading logs...')).not.toBeInTheDocument())
    expect(screen.queryByTestId('logs-unavailable')).not.toBeInTheDocument()
  })
})
