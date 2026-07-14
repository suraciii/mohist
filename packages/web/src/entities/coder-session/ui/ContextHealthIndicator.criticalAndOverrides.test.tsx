import '@testing-library/jest-dom'
import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { ContextHealthIndicator } from './ContextHealthIndicator'

describe('ContextHealthIndicator — critical threshold', () => {
  describe('red (critical) threshold', () => {
    it('renders with data-status="red" and data-severity="critical"', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={950_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={95}
          healthStatus="red"
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
          healthStatus="red"
        />,
      )
      const indicator = screen.getByTestId('context-health-indicator')
      expect(indicator.className).toContain('text-danger')
      const dot = indicator.querySelector('span[aria-hidden="true"]')
      expect(dot?.className).toContain('bg-danger')
    })

    it('renders an error glyph', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={950_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={95}
          healthStatus="red"
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
          healthStatus="red"
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
          healthStatus="red"
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
          healthStatus="red"
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

  describe('overrides and server-path tests', () => {
    it('hides absolute token counts by default', () => {
      render(
        <ContextHealthIndicator
          contextWindowUsed={450_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={45}
          healthStatus="green"
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
          healthStatus="green"
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
          healthStatus="red"
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
          healthStatus="green"
        />,
      )
      const indicator = screen.getByTestId('context-health-indicator')
      expect(indicator).toHaveAttribute('title', 'Context usage 45%')
    })

    it('uses the server-provided healthStatus over percents-only classification', () => {
      // contextUsagePercent=45 on its own would be 'green', but
      // server healthStatus says 'red' — server wins.
      render(
        <ContextHealthIndicator
          contextWindowUsed={200_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={85}
          healthStatus="red"
        />,
      )
      const indicator = screen.getByTestId('context-health-indicator')
      expect(indicator).toHaveAttribute('data-status', 'red')
      expect(indicator).toHaveTextContent('85%')
    })

    it('degrades gracefully without fabricating a status when healthStatus is absent', () => {
      const { container } = render(
        <ContextHealthIndicator
          contextWindowUsed={720_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={72}
        />,
      )
      expect(container.firstChild).toBeNull()
      expect(screen.queryByTestId('context-health-indicator')).toBeNull()
    })

    it('does not render data-status="yellow" from percent-only input', () => {
      const { container } = render(
        <ContextHealthIndicator
          contextWindowUsed={720_000}
          contextWindowSize={1_000_000}
          contextUsagePercent={72}
          healthStatus={null}
        />,
      )
      expect(container.querySelector('[data-status="yellow"]')).toBeNull()
    })

    it('hides when contextUsagePercent is absent even if healthStatus is present', () => {
      const { container } = render(
        <ContextHealthIndicator
          healthStatus="red"
        />,
      )
      expect(container.firstChild).toBeNull()
    })
  })
})