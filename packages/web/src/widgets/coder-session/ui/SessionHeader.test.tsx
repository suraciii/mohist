// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import type { CoderSessionSummary } from '../../../entities/coder-session'
import { SessionHeader } from './SessionHeader'

type SessionHeaderOverrides = Partial<CoderSessionSummary> & {
  resolvedModel?: string | null
  inputTokens?: number | null
  outputTokens?: number | null
  totalTokens?: number | null
  cachedReadTokens?: number | null
  thoughtTokens?: number | null
  costAmount?: number | null
  costCurrency?: string | null
  contextWindowUsed?: number | null
  contextWindowSize?: number | null
  contextUsagePercent?: number | null
  failureCategory?: string | null
  toolCallCount?: number | null
  toolErrorCount?: number | null
}

function makeSession(overrides: SessionHeaderOverrides = {}): CoderSessionSummary {
  const {
    resolvedModel,
    failureCategory,
    toolCallCount,
    toolErrorCount,
    inputTokens,
    outputTokens,
    totalTokens,
    cachedReadTokens,
    thoughtTokens,
    costAmount,
    costCurrency,
    contextWindowUsed,
    contextWindowSize,
    contextUsagePercent,
    ...rest
  } = overrides
  return {
    id: 'session-1',
    sessionName: 'session-1',
    acpSessionId: 'acp-1',
    status: 'completed',
    model: null,
    executionId: null,
    taskDescription: null,
    createdAt: '2026-01-01T00:00:00Z',
    completedAt: '2026-01-01T00:10:00Z',
    coderType: null,
    stage: 'check',
    title: 'Check the fix',
    lastDataAt: '2026-01-01T00:10:00Z',
    probeSentAt: null,
    probeDeadlineAt: null,
    failureReason: null,
    eventSummary: {
      resolvedModel: resolvedModel ?? null,
      failureCategory: failureCategory ?? null,
      toolCallCount: toolCallCount ?? null,
      toolErrorCount: toolErrorCount ?? null,
    },
    usage: {
      inputTokens: inputTokens ?? null,
      outputTokens: outputTokens ?? null,
      totalTokens: totalTokens ?? null,
      cachedReadTokens: cachedReadTokens ?? null,
      thoughtTokens: thoughtTokens ?? null,
      costAmount: costAmount ?? null,
      costCurrency: costCurrency ?? null,
      contextWindowUsed: contextWindowUsed ?? null,
      contextWindowSize: contextWindowSize ?? null,
      contextUsagePercent: contextUsagePercent ?? null,
    },
    ...rest,
  }
}

describe('SessionHeader context health', () => {
  it('hides the context health indicator when window size is zero', () => {
    render(
      <MemoryRouter>
        <SessionHeader
          session={makeSession({
            contextWindowUsed: 100,
            contextWindowSize: 0,
          })}
          issueNumber={12}
        />
      </MemoryRouter>,
    )
    expect(screen.queryByTestId('context-health-indicator')).toBeNull()
  })

  it('hides the context health indicator when contextWindowSize is null', () => {
    render(
      <MemoryRouter>
        <SessionHeader
          session={makeSession({ contextWindowSize: null })}
          issueNumber={12}
        />
      </MemoryRouter>,
    )
    expect(screen.queryByTestId('context-health-indicator')).toBeNull()
  })

  it('renders a green dot indicator at low usage', () => {
    render(
      <MemoryRouter>
        <SessionHeader
          session={makeSession({
            contextWindowUsed: 300_000,
            contextWindowSize: 1_000_000,
            contextUsagePercent: 30,
          })}
          issueNumber={12}
        />
      </MemoryRouter>,
    )
    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator).toHaveAttribute('data-status', 'green')
    expect(indicator).toHaveTextContent('30%')
  })

  it('renders a yellow dot indicator at moderate usage', () => {
    render(
      <MemoryRouter>
        <SessionHeader
          session={makeSession({
            contextWindowUsed: 720_000,
            contextWindowSize: 1_000_000,
            contextUsagePercent: 72,
          })}
          issueNumber={12}
        />
      </MemoryRouter>,
    )
    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator).toHaveAttribute('data-status', 'yellow')
    expect(indicator).toHaveTextContent('72%')
  })

  it('renders a red dot indicator at high usage', () => {
    render(
      <MemoryRouter>
        <SessionHeader
          session={makeSession({
            contextWindowUsed: 950_000,
            contextWindowSize: 1_000_000,
            contextUsagePercent: 95,
          })}
          issueNumber={12}
        />
      </MemoryRouter>,
    )
    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator).toHaveAttribute('data-status', 'red')
    expect(indicator).toHaveTextContent('95%')
  })
})
