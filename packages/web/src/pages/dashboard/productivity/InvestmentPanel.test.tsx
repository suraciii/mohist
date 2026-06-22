// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'

import { InvestmentPanel } from './InvestmentPanel'

function renderPanel() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider>
        <MemoryRouter initialEntries={['/']}>
          <InvestmentPanel />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('InvestmentPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  afterEach(() => {
    cleanup()
  })

  it('is collapsed on first render with the toggle exposing aria-expanded=false', () => {
    renderPanel()

    const section = screen.getByTestId('productivity-investment')
    expect(section).toBeInTheDocument()

    const toggle = screen.getByTestId('productivity-investment-toggle')
    expect(toggle).toHaveAttribute('aria-expanded', 'false')

    expect(screen.queryByTestId('productivity-investment-body')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-investment-caliber')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-investment-empty')).not.toBeInTheDocument()

    expect(section).not.toHaveAttribute('data-state', 'empty')
  })

  it('reveals a labeled caliber annotation when the panel is expanded', () => {
    renderPanel()

    const toggle = screen.getByTestId('productivity-investment-toggle')
    fireEvent.click(toggle)

    expect(toggle).toHaveAttribute('aria-expanded', 'true')

    const body = screen.getByTestId('productivity-investment-body')
    expect(body).toBeInTheDocument()

    const caliber = screen.getByTestId('productivity-investment-caliber')
    expect(caliber).toBeInTheDocument()

    const label = screen.getByTestId('productivity-investment-caliber-label')
    expect(label).toBeInTheDocument()
    expect(label.textContent ?? '').toMatch(/window/i)
    expect(label.textContent ?? '').toMatch(/population/i)

    const value = screen.getByTestId('productivity-investment-caliber-value')
    expect(value).toBeInTheDocument()
    expect(value.textContent ?? '').not.toMatch(/^\s*$/)
  })

  it('renders an explicitly labeled data-unavailable empty state (no blank panel)', () => {
    renderPanel()

    fireEvent.click(screen.getByTestId('productivity-investment-toggle'))

    const empty = screen.getByTestId('productivity-investment-empty')
    expect(empty).toBeInTheDocument()
    expect(empty).toHaveAttribute('data-state', 'empty')
    expect(empty.textContent ?? '').toMatch(/data unavailable/i)
  })

  it('collapses again on a second toggle click and hides the body', () => {
    renderPanel()

    const toggle = screen.getByTestId('productivity-investment-toggle')
    fireEvent.click(toggle)
    expect(toggle).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByTestId('productivity-investment-body')).toBeInTheDocument()

    fireEvent.click(toggle)
    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    expect(screen.queryByTestId('productivity-investment-body')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-investment-caliber')).not.toBeInTheDocument()
    expect(screen.queryByTestId('productivity-investment-empty')).not.toBeInTheDocument()
  })
})
