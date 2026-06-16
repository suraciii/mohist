// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { ContextHealthIndicator } from './ContextHealthIndicator'

describe('ContextHealthIndicator', () => {
  it('renders nothing when no context data is available', () => {
    const { container } = render(<ContextHealthIndicator />)
    expect(container.firstChild).toBeNull()
  })

  it('renders nothing when window size is zero and no explicit percent', () => {
    const { container } = render(
      <ContextHealthIndicator
        contextWindowUsed={500_000}
        contextWindowSize={0}
      />,
    )
    expect(container.firstChild).toBeNull()
  })

  it('renders a green dot and percentage at low usage', () => {
    render(
      <ContextHealthIndicator
        contextWindowUsed={450_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={45}
      />,
    )
    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator).toHaveAttribute('data-status', 'green')
    expect(indicator).toHaveTextContent('45%')
    expect(indicator).toHaveAttribute('aria-label', 'Context usage 45%')
  })

  it('renders a yellow dot and percentage at moderate usage', () => {
    render(
      <ContextHealthIndicator
        contextWindowUsed={720_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={72}
      />,
    )
    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator).toHaveAttribute('data-status', 'yellow')
    expect(indicator).toHaveTextContent('72%')
  })

  it('renders a red dot and percentage at high usage', () => {
    render(
      <ContextHealthIndicator
        contextWindowUsed={950_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={95}
      />,
    )
    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator).toHaveAttribute('data-status', 'red')
    expect(indicator).toHaveTextContent('95%')
  })

  it('hides absolute token counts by default', () => {
    render(
      <ContextHealthIndicator
        contextWindowUsed={450_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={45}
      />,
    )
    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator.textContent).not.toMatch(/450/)
  })

  it('shows absolute token counts when showTokens is true', () => {
    render(
      <ContextHealthIndicator
        contextWindowUsed={450_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={45}
        showTokens
      />,
    )
    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator.textContent).toMatch(/450/)
    expect(indicator.textContent).toMatch(/1\.0M/)
  })

  it('respects an explicit ariaLabel override', () => {
    render(
      <ContextHealthIndicator
        contextWindowUsed={450_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={45}
        ariaLabel="Custom label"
      />,
    )
    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator).toHaveAttribute('aria-label', 'Custom label')
    expect(indicator).toHaveAttribute('title', 'Custom label')
  })

  it('uses title attribute for hover accessibility', () => {
    render(
      <ContextHealthIndicator
        contextWindowUsed={450_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={45}
      />,
    )
    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator).toHaveAttribute('title', 'Context usage 45%')
  })

  it('derives percent from used/size when explicit percent is missing', () => {
    render(
      <ContextHealthIndicator
        contextWindowUsed={720_000}
        contextWindowSize={1_000_000}
      />,
    )
    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator).toHaveAttribute('data-status', 'yellow')
    expect(indicator).toHaveTextContent('72%')
  })

  it('respects the explicit percent even when the implied value would differ', () => {
    // 200_000 / 1_000_000 = 20% (green), but explicit says 85% (red).
    render(
      <ContextHealthIndicator
        contextWindowUsed={200_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={85}
      />,
    )
    const indicator = screen.getByTestId('context-health-indicator')
    expect(indicator).toHaveAttribute('data-status', 'red')
    expect(indicator).toHaveTextContent('85%')
  })
})
