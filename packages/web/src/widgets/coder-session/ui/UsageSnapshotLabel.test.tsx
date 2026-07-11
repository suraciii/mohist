import '@testing-library/jest-dom'
import { render, screen } from '@testing-library/react'
import { describe, it, expect } from 'vitest'
import { UsageSnapshotLabel } from './UsageSnapshotLabel'
import type { UsageSnapshot } from '../model/usage-snapshot'

describe('UsageSnapshotLabel', () => {
  it('shows "activity window only" scope qualifier', () => {
    const snapshot: UsageSnapshot = {
      inputTokens: 100,
      outputTokens: 50,
      totalTokens: 150,
      costAmount: 0,
      costCurrency: null,
    }

    render(<UsageSnapshotLabel snapshot={snapshot} />)
    expect(screen.getByText('activity window only')).toBeInTheDocument()
  })

  it('shows token totals when present', () => {
    const snapshot: UsageSnapshot = {
      inputTokens: 1000,
      outputTokens: 500,
      totalTokens: 1500,
      costAmount: 0,
      costCurrency: null,
    }

    render(<UsageSnapshotLabel snapshot={snapshot} />)
    expect(screen.getByText(/1.5k total tokens/)).toBeInTheDocument()
  })

  it('shows cost when present', () => {
    const snapshot: UsageSnapshot = {
      inputTokens: 100,
      outputTokens: 50,
      totalTokens: 150,
      costAmount: 0.18,
      costCurrency: 'USD',
    }

    render(<UsageSnapshotLabel snapshot={snapshot} />)
    expect(screen.getByText(/\$0\.18/)).toBeInTheDocument()
  })

  it('does not label totals as project-total', () => {
    const snapshot: UsageSnapshot = {
      inputTokens: 100,
      outputTokens: 50,
      totalTokens: 150,
      costAmount: 0,
      costCurrency: null,
    }

    render(<UsageSnapshotLabel snapshot={snapshot} />)
    expect(screen.queryByText(/project.?total/i)).toBeNull()
  })

  it('does not label totals as weekly-total', () => {
    const snapshot: UsageSnapshot = {
      inputTokens: 100,
      outputTokens: 50,
      totalTokens: 150,
      costAmount: 0,
      costCurrency: null,
    }

    render(<UsageSnapshotLabel snapshot={snapshot} />)
    expect(screen.queryByText(/weekly.?total/i)).toBeNull()
  })

  it('does not label totals as all-time', () => {
    const snapshot: UsageSnapshot = {
      inputTokens: 100,
      outputTokens: 50,
      totalTokens: 150,
      costAmount: 0,
      costCurrency: null,
    }

    render(<UsageSnapshotLabel snapshot={snapshot} />)
    expect(screen.queryByText(/all.?time/i)).toBeNull()
  })

  it('shows no-tokens message when all values are zero', () => {
    const snapshot: UsageSnapshot = {
      inputTokens: 0,
      outputTokens: 0,
      totalTokens: 0,
      costAmount: 0,
      costCurrency: null,
    }

    render(<UsageSnapshotLabel snapshot={snapshot} />)
    expect(screen.getByText('No usage data')).toBeInTheDocument()
  })
})
