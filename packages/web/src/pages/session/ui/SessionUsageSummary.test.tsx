import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { SessionUsageSummary } from './SessionUsageSummary'
import type { AgentSessionUsage } from '../../../entities/coder-session'

function fullUsage(overrides: Partial<AgentSessionUsage> = {}): AgentSessionUsage {
  return {
    inputTokens: 1500,
    outputTokens: 3200,
    totalTokens: 4700,
    cachedReadTokens: 800,
    cachedWriteTokens: 300,
    thoughtTokens: 1200,
    costAmount: 0.18,
    costCurrency: 'USD',
    contextWindowUsed: 24000,
    contextWindowSize: 32000,
    contextUsagePercent: 75,
    healthStatus: 'yellow',
    ...overrides,
  }
}

describe('SessionUsageSummary', () => {
  describe('all fields visible', () => {
    it('renders input, output, and total tokens', () => {
      render(<SessionUsageSummary usage={fullUsage()} />)
      expect(screen.getByTestId('usage-summary-input')).toHaveTextContent('1.5k in')
      expect(screen.getByTestId('usage-summary-output')).toHaveTextContent('· 3.2k out')
      expect(screen.getByTestId('usage-summary-total')).toHaveTextContent('· 4.7k total')
    })

    it('renders cached and thought tokens', () => {
      render(<SessionUsageSummary usage={fullUsage()} />)
      expect(screen.getByTestId('usage-summary-cached')).toHaveTextContent('· 800 cached')
      expect(screen.getByTestId('usage-summary-cache-write')).toHaveTextContent('· 300 cache write')
      expect(screen.getByTestId('usage-summary-thought')).toHaveTextContent('· 1.2k thought')
    })

    it('renders cost', () => {
      render(<SessionUsageSummary usage={fullUsage()} />)
      expect(screen.getByTestId('usage-summary-cost')).toHaveTextContent('$0.18')
    })

    it('renders context window with percentage', () => {
      render(<SessionUsageSummary usage={fullUsage()} />)
      const ctx = screen.getByTestId('usage-summary-context')
      expect(ctx).toHaveTextContent('24.0k / 32.0k')
      expect(ctx).toHaveTextContent('(75%)')
    })

    it('renders health status indicator', () => {
      render(<SessionUsageSummary usage={fullUsage()} />)
      const health = screen.getByTestId('usage-summary-health')
      expect(health).not.toBeNull()
    })

    it('renders the summary container', () => {
      render(<SessionUsageSummary usage={fullUsage()} />)
      expect(screen.getByTestId('session-usage-summary')).not.toBeNull()
    })
  })

  describe('cache-saved tokens surfaced', () => {
    it('shows cached tokens when present and > 0', () => {
      render(<SessionUsageSummary usage={fullUsage({ cachedReadTokens: 900 })} />)
      expect(screen.getByTestId('usage-summary-cached')).toHaveTextContent('· 900 cached')
    })

    it('omits cached tokens when zero', () => {
      render(<SessionUsageSummary usage={fullUsage({ cachedReadTokens: 0 })} />)
      expect(screen.queryByTestId('usage-summary-cached')).toBeNull()
    })

    it('omits cached tokens when null', () => {
      render(<SessionUsageSummary usage={fullUsage({ cachedReadTokens: null })} />)
      expect(screen.queryByTestId('usage-summary-cached')).toBeNull()
    })

    it('omits cached tokens when undefined', () => {
      render(<SessionUsageSummary usage={fullUsage({ cachedReadTokens: undefined })} />)
      expect(screen.queryByTestId('usage-summary-cached')).toBeNull()
    })

    it('omits cache-write tokens when zero', () => {
      render(<SessionUsageSummary usage={fullUsage({ cachedWriteTokens: 0 })} />)
      expect(screen.queryByTestId('usage-summary-cache-write')).toBeNull()
    })
  })

  describe('reasoning tokens surfaced', () => {
    it('shows thought tokens when present and > 0', () => {
      render(<SessionUsageSummary usage={fullUsage({ thoughtTokens: 500 })} />)
      expect(screen.getByTestId('usage-summary-thought')).toHaveTextContent('· 500 thought')
    })

    it('omits thought tokens when zero', () => {
      render(<SessionUsageSummary usage={fullUsage({ thoughtTokens: 0 })} />)
      expect(screen.queryByTestId('usage-summary-thought')).toBeNull()
    })

    it('omits thought tokens when null', () => {
      render(<SessionUsageSummary usage={fullUsage({ thoughtTokens: null })} />)
      expect(screen.queryByTestId('usage-summary-thought')).toBeNull()
    })

    it('omits thought tokens when undefined', () => {
      render(<SessionUsageSummary usage={fullUsage({ thoughtTokens: undefined })} />)
      expect(screen.queryByTestId('usage-summary-thought')).toBeNull()
    })
  })

  describe('missing fields degrade gracefully', () => {
    it('renders nothing when all usage fields are null', () => {
      const { container } = render(<SessionUsageSummary usage={fullUsage({
        inputTokens: null,
        outputTokens: null,
        totalTokens: null,
        cachedReadTokens: null,
        cachedWriteTokens: null,
        thoughtTokens: null,
        costAmount: null,
        contextWindowUsed: null,
      })} />)
      expect(container.firstChild).toBeNull()
    })

    it('renders nothing when usage is undefined', () => {
      const { container } = render(<SessionUsageSummary usage={undefined} />)
      expect(container.firstChild).toBeNull()
    })

    it('renders nothing when usage is null', () => {
      const { container } = render(<SessionUsageSummary usage={null} />)
      expect(container.firstChild).toBeNull()
    })

    it('omits context window when contextWindowUsed is null', () => {
      render(<SessionUsageSummary usage={fullUsage({ contextWindowUsed: null })} />)
      expect(screen.queryByTestId('usage-summary-context')).toBeNull()
    })

    it('omits cost when costAmount is null', () => {
      render(<SessionUsageSummary usage={fullUsage({ costAmount: null })} />)
      expect(screen.queryByTestId('usage-summary-cost')).toBeNull()
    })

    it('omits health indicator when contextUsagePercent is null', () => {
      render(<SessionUsageSummary usage={fullUsage({ contextUsagePercent: null })} />)
      expect(screen.queryByTestId('usage-summary-health')).toBeNull()
    })
  })

  describe('no placeholder stub', () => {
    it('does not render a literal label when there is usage data', () => {
      render(<SessionUsageSummary usage={fullUsage()} />)
      expect(screen.getByTestId('session-usage-summary')).toHaveTextContent('Tokens:')
    })

    it('renders nothing when usage data is absent (replaces dead stub)', () => {
      const { container } = render(<SessionUsageSummary usage={null} />)
      expect(container.firstChild).toBeNull()
    })
  })
})
