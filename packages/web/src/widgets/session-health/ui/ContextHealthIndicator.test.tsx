// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { ContextHealthIndicator } from './ContextHealthIndicator'

describe('ContextHealthIndicator', () => {
  describe('missing / non-finite data', () => {
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

    it('renders nothing when explicit percent is NaN and used/size are missing', () => {
      const { container } = render(
        <ContextHealthIndicator
          contextWindowUsed={null}
          contextWindowSize={null}
          contextUsagePercent={Number.NaN}
        />,
      )
      expect(container.firstChild).toBeNull()
    })

    it('renders nothing when explicit percent is Infinity and used/size are missing', () => {
      const { container } = render(
        <ContextHealthIndicator
          contextWindowUsed={null}
          contextWindowSize={null}
          contextUsagePercent={Number.POSITIVE_INFINITY}
        />,
      )
      expect(container.firstChild).toBeNull()
    })

    it('renders nothing when used/size are non-finite', () => {
      const { container } = render(
        <ContextHealthIndicator
          contextWindowUsed={Number.NaN}
          contextWindowSize={1_000_000}
        />,
      )
      expect(container.firstChild).toBeNull()
    })
  })

  describe('green (healthy) threshold is quiet', () => {
    it('renders with data-status="green" and a simple percent tooltip', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={450_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={45}
        />,
      )
      const indicator = screen.getByTestId('context-health-indicator')
      expect(indicator).toHaveAttribute('data-status', 'green')
      expect(indicator).toHaveAttribute('data-severity', 'ok')
      expect(indicator).toHaveTextContent('45%')
      expect(indicator).toHaveAttribute('aria-label', 'Context usage 45%')
      expect(indicator).toHaveAttribute('title', 'Context usage 45%')
    })

    it('does not render an alert role or aria-live', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={450_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={45}
        />,
      )
      const indicator = screen.getByTestId('context-health-indicator')
      expect(indicator).not.toHaveAttribute('role')
      expect(indicator).not.toHaveAttribute('aria-live')
    })

    it('does not render a severity glyph', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={450_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={45}
        />,
      )
      expect(screen.queryByTestId('context-health-glyph')).toBeNull()
    })

    it('uses neutral gray text (no warning color)', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={450_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={45}
        />,
      )
      const indicator = screen.getByTestId('context-health-indicator')
      expect(indicator.className).toContain('text-gray-600')
      expect(indicator.className).not.toContain('text-green-')
      expect(indicator.className).not.toContain('text-yellow-')
      expect(indicator.className).not.toContain('text-red-')
    })

    it('uses neutral gray dot (no warning color)', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={450_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={45}
        />,
      )
      const dot = screen.getByTestId('context-health-indicator').querySelector('span[aria-hidden="true"]')
      expect(dot?.className).toContain('bg-gray-400')
      expect(dot?.className).not.toContain('bg-yellow-')
      expect(dot?.className).not.toContain('bg-red-')
      expect(dot?.className).not.toContain('bg-green-')
    })
  })

  describe('yellow (warning) threshold', () => {
    it('renders with data-status="yellow" and data-severity="warning"', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={720_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={72}
        />,
      )
      const indicator = screen.getByTestId('context-health-indicator')
      expect(indicator).toHaveAttribute('data-status', 'yellow')
      expect(indicator).toHaveAttribute('data-severity', 'warning')
      expect(indicator).toHaveTextContent('72%')
    })

    it('uses yellow text color and yellow dot color', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={720_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={72}
        />,
      )
      const indicator = screen.getByTestId('context-health-indicator')
      expect(indicator.className).toContain('text-yellow-')
      const dot = indicator.querySelector('span[aria-hidden="true"]')
      expect(dot?.className).toContain('bg-yellow-')
    })

    it('renders a warning glyph', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={720_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={72}
        />,
      )
      const glyph = screen.getByTestId('context-health-glyph')
      expect(glyph).toBeInTheDocument()
      expect(glyph).toHaveAttribute('aria-hidden', 'true')
      expect(glyph.tagName.toLowerCase()).toBe('svg')
    })

    it('uses role="status" without aria-live', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={720_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={72}
        />,
      )
      const indicator = screen.getByTestId('context-health-indicator')
      expect(indicator).toHaveAttribute('role', 'status')
      expect(indicator).not.toHaveAttribute('aria-live')
    })

    it('exposes a descriptive severity tooltip', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={720_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={72}
        />,
      )
      const indicator = screen.getByTestId('context-health-indicator')
      expect(indicator).toHaveAttribute('title', 'Context window 72% full — near limit')
      expect(indicator).toHaveAttribute('aria-label', 'Context window 72% full — near limit')
    })

    it('applies alert treatment at the 60% boundary', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={600_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={60}
        />,
      )
      const indicator = screen.getByTestId('context-health-indicator')
      expect(indicator).toHaveAttribute('data-status', 'yellow')
      expect(indicator).toHaveAttribute('data-severity', 'warning')
      expect(indicator).toHaveAttribute('role', 'status')
      expect(indicator).toHaveAttribute('title', 'Context window 60% full — near limit')
    })
  })

  describe('red (critical) threshold', () => {
    it('renders with data-status="red" and data-severity="critical"', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={950_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={95}
        />,
      )
      const indicator = screen.getByTestId('context-health-indicator')
      expect(indicator).toHaveAttribute('data-status', 'red')
      expect(indicator).toHaveAttribute('data-severity', 'critical')
      expect(indicator).toHaveTextContent('95%')
    })

    it('uses red text color and red dot color', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={950_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={95}
        />,
      )
      const indicator = screen.getByTestId('context-health-indicator')
      expect(indicator.className).toContain('text-red-')
      const dot = indicator.querySelector('span[aria-hidden="true"]')
      expect(dot?.className).toContain('bg-red-')
    })

    it('renders an error glyph', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={950_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={95}
        />,
      )
      const glyph = screen.getByTestId('context-health-glyph')
      expect(glyph).toBeInTheDocument()
      expect(glyph).toHaveAttribute('aria-hidden', 'true')
      expect(glyph.tagName.toLowerCase()).toBe('svg')
    })

    it('uses role="alert" and aria-live="polite"', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={950_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={95}
        />,
      )
      const indicator = screen.getByTestId('context-health-indicator')
      expect(indicator).toHaveAttribute('role', 'alert')
      expect(indicator).toHaveAttribute('aria-live', 'polite')
    })

    it('exposes a descriptive severity tooltip', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={950_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={95}
        />,
      )
      const indicator = screen.getByTestId('context-health-indicator')
      expect(indicator).toHaveAttribute('title', 'Context window 95% full — at limit, compact or reset recommended')
      expect(indicator).toHaveAttribute('aria-label', 'Context window 95% full — at limit, compact or reset recommended')
    })

    it('applies critical alert treatment at the 80% boundary', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={800_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={80}
        />,
      )
      const indicator = screen.getByTestId('context-health-indicator')
      expect(indicator).toHaveAttribute('data-status', 'red')
      expect(indicator).toHaveAttribute('data-severity', 'critical')
      expect(indicator).toHaveAttribute('role', 'alert')
      expect(indicator).toHaveAttribute('aria-live', 'polite')
      expect(indicator).toHaveAttribute('title', 'Context window 80% full — at limit, compact or reset recommended')
    })
  })

  describe('overrides and derivation', () => {
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

    it('respects an explicit ariaLabel override even at red', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={950_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={95}
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
})