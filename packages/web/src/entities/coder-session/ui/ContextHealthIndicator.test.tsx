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

    it('renders nothing when window size is zero and no explicit percent or healthStatus', () => {
      const { container } = render(
        <ContextHealthIndicator
          contextWindowUsed={500_000}
          contextWindowSize={0}
        />,
      )
      // contextUsagePercent is absent → indicator hides
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

    it('renders nothing when contextUsagePercent is non-finite', () => {
      const { container } = render(
        <ContextHealthIndicator
          contextWindowUsed={Number.NaN}
          contextWindowSize={1_000_000}
          contextUsagePercent={Number.NaN}
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
          healthStatus="green"
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
          healthStatus="green"
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
          healthStatus="green"
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
          healthStatus="green"
        />,
      )
      const indicator = screen.getByTestId('context-health-indicator')
      expect(indicator.className).toContain('text-muted-foreground')
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
          healthStatus="green"
        />,
      )
      const dot = screen.getByTestId('context-health-indicator').querySelector('span[aria-hidden="true"]')
      expect(dot?.className).toContain('bg-muted-foreground/60')
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
          healthStatus="yellow"
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
          healthStatus="yellow"
        />,
      )
      const indicator = screen.getByTestId('context-health-indicator')
      expect(indicator.className).toContain('text-warning')
      const dot = indicator.querySelector('span[aria-hidden="true"]')
      expect(dot?.className).toContain('bg-warning')
    })

    it('renders a warning glyph', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={720_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={72}
          healthStatus="yellow"
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
          healthStatus="yellow"
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
          healthStatus="yellow"
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
          healthStatus="yellow"
        />,
      )
      const indicator = screen.getByTestId('context-health-indicator')
      expect(indicator).toHaveAttribute('data-status', 'yellow')
      expect(indicator).toHaveAttribute('data-severity', 'warning')
      expect(indicator).toHaveAttribute('role', 'status')
      expect(indicator).toHaveAttribute('title', 'Context window 60% full — near limit')
    })
  })
})

