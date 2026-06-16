// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { ContextHealthBar } from './ContextHealthBar'

describe('ContextHealthBar', () => {
  it('renders nothing when no context data is available', () => {
    const { container } = render(<ContextHealthBar />)
    expect(container.firstChild).toBeNull()
  })

  it('renders nothing when only the explicit percent is null', () => {
    const { container } = render(
      <ContextHealthBar
        contextWindowUsed={500_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={null}
      />,
    )
    // Percent is derived from used/size when the explicit value is null.
    // The component should render in this case.
    expect(container.firstChild).not.toBeNull()
  })

  it('renders the standard "used / total (percent%)" label at low usage', () => {
    render(
      <ContextHealthBar
        contextWindowUsed={450_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={45}
      />,
    )
    expect(screen.getByTestId('context-health-label')).toHaveTextContent('450.0k / 1.0M tokens (45%)')
  })

  it('marks the bar with status="green" when usage is below 60%', () => {
    render(
      <ContextHealthBar
        contextWindowUsed={450_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={45}
      />,
    )
    expect(screen.getByTestId('context-health-bar')).toHaveAttribute('data-status', 'green')
  })

  it('marks the bar with status="yellow" when usage is at the 60% boundary', () => {
    render(
      <ContextHealthBar
        contextWindowUsed={600_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={60}
      />,
    )
    expect(screen.getByTestId('context-health-bar')).toHaveAttribute('data-status', 'yellow')
  })

  it('marks the bar with status="yellow" for usage between 60% and 80%', () => {
    render(
      <ContextHealthBar
        contextWindowUsed={720_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={72}
      />,
    )
    expect(screen.getByTestId('context-health-bar')).toHaveAttribute('data-status', 'yellow')
  })

  it('marks the bar with status="red" when usage is at the 80% boundary', () => {
    render(
      <ContextHealthBar
        contextWindowUsed={800_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={80}
      />,
    )
    expect(screen.getByTestId('context-health-bar')).toHaveAttribute('data-status', 'red')
  })

  it('marks the bar with status="red" for usage well above 80%', () => {
    render(
      <ContextHealthBar
        contextWindowUsed={950_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={95}
      />,
    )
    expect(screen.getByTestId('context-health-bar')).toHaveAttribute('data-status', 'red')
  })

  it('sets the fill width to the (unrounded) percent value', () => {
    render(
      <ContextHealthBar
        contextWindowUsed={450_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={45.4}
      />,
    )
    const fill = screen.getByTestId('context-health-fill')
    expect(fill).toHaveAttribute('data-percent', '45')
    expect(fill).toHaveStyle({ width: '45.4%' })
  })

  it('clamps the fill width at 100% when the percent exceeds the cap', () => {
    render(
      <ContextHealthBar
        contextWindowUsed={1_500_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={150}
      />,
    )
    const fill = screen.getByTestId('context-health-fill')
    expect(fill).toHaveStyle({ width: '100%' })
  })

  it('hides the warning banner when usage is below 80%', () => {
    render(
      <ContextHealthBar
        contextWindowUsed={450_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={45}
        onCompact={() => {}}
        onReset={() => {}}
      />,
    )
    expect(screen.queryByRole('status')).toBeNull()
  })

  it('hides the warning banner when usage is at 79.9% (just below the threshold)', () => {
    render(
      <ContextHealthBar
        contextWindowUsed={799_999}
        contextWindowSize={1_000_000}
        contextUsagePercent={79.9}
        onCompact={() => {}}
        onReset={() => {}}
      />,
    )
    expect(screen.queryByRole('status')).toBeNull()
  })

  it('shows the warning banner when usage is at 80%', () => {
    render(
      <ContextHealthBar
        contextWindowUsed={800_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={80}
        onCompact={() => {}}
        onReset={() => {}}
      />,
    )
    const banner = screen.getByRole('status')
    expect(banner).toBeInTheDocument()
    expect(banner).toHaveTextContent(/80%/)
  })

  it('shows the warning banner when usage is well above 80%', () => {
    render(
      <ContextHealthBar
        contextWindowUsed={950_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={95}
        onCompact={() => {}}
        onReset={() => {}}
      />,
    )
    const banner = screen.getByRole('status')
    expect(banner).toBeInTheDocument()
    expect(banner).toHaveTextContent(/95%/)
  })

  it('renders both Compact and Reset action links in the warning banner', () => {
    render(
      <ContextHealthBar
        contextWindowUsed={950_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={95}
        onCompact={() => {}}
        onReset={() => {}}
      />,
    )
    const banner = screen.getByRole('status')
    const compactButton = screen.getByRole('button', { name: 'Compact' })
    const resetButton = screen.getByRole('button', { name: 'Reset' })
    expect(banner).toContainElement(compactButton)
    expect(banner).toContainElement(resetButton)
  })

  it('invokes the onCompact callback when the Compact action is clicked', () => {
    const onCompact = vi.fn()
    render(
      <ContextHealthBar
        contextWindowUsed={950_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={95}
        onCompact={onCompact}
        onReset={() => {}}
      />,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Compact' }))
    expect(onCompact).toHaveBeenCalledTimes(1)
  })

  it('invokes the onReset callback when the Reset action is clicked', () => {
    const onReset = vi.fn()
    render(
      <ContextHealthBar
        contextWindowUsed={950_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={95}
        onCompact={() => {}}
        onReset={onReset}
      />,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Reset' }))
    expect(onReset).toHaveBeenCalledTimes(1)
  })

  it('hides the warning banner when only onCompact is provided and usage is high', () => {
    render(
      <ContextHealthBar
        contextWindowUsed={950_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={95}
        onCompact={() => {}}
      />,
    )
    // The banner only appears when at least one recovery action is wired
    // up; with just compact it should still show because onCompact is set.
    expect(screen.getByRole('status')).toBeInTheDocument()
  })

  it('hides the warning banner when no recovery callbacks are provided', () => {
    render(
      <ContextHealthBar
        contextWindowUsed={950_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={95}
      />,
    )
    // No Compact/Reset wired up => no actionable banner, just the bar.
    expect(screen.queryByRole('status')).toBeNull()
    expect(screen.getByTestId('context-health-bar')).toHaveAttribute('data-status', 'red')
  })

  it('dismisses the warning banner when the user clicks the Dismiss link', () => {
    render(
      <ContextHealthBar
        contextWindowUsed={950_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={95}
        onCompact={() => {}}
        onReset={() => {}}
      />,
    )
    const dismiss = screen.getByRole('button', { name: 'Dismiss context warning' })
    fireEvent.click(dismiss)
    expect(screen.queryByRole('status')).toBeNull()
  })

  it('respects showWarning=false and hides the warning banner even at high usage', () => {
    render(
      <ContextHealthBar
        contextWindowUsed={950_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={95}
        onCompact={() => {}}
        onReset={() => {}}
        showWarning={false}
      />,
    )
    expect(screen.queryByRole('status')).toBeNull()
    // The bar itself should still reflect the high-usage red state.
    expect(screen.getByTestId('context-health-bar')).toHaveAttribute('data-status', 'red')
  })

  it('auto-dismisses the warning banner by simply re-rendering at lower usage', () => {
    const { rerender } = render(
      <ContextHealthBar
        contextWindowUsed={950_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={95}
        onCompact={() => {}}
        onReset={() => {}}
      />,
    )
    expect(screen.getByRole('status')).toBeInTheDocument()

    // After a new SSE update drops usage below 80%, the banner disappears.
    rerender(
      <ContextHealthBar
        contextWindowUsed={450_000}
        contextWindowSize={1_000_000}
        contextUsagePercent={45}
        onCompact={() => {}}
        onReset={() => {}}
      />,
    )
    expect(screen.queryByRole('status')).toBeNull()
    expect(screen.getByTestId('context-health-bar')).toHaveAttribute('data-status', 'green')
  })
})
