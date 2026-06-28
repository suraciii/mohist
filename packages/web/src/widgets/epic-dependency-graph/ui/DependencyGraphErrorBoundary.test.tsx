// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { useEffect } from 'react'
import { DependencyGraphErrorBoundary } from './DependencyGraphErrorBoundary'

function ThrowingWidget({ message }: { message: string }): never {
  throw new Error(message)
}

function SafeWidget() {
  return <div data-testid="safe-widget">Safe widget content</div>
}

function RenderabilityNotifyingWidget({ reason }: { reason: 'cyclic' | 'empty' | 'renderable' }) {
  const onRenderabilityChange = useWidgetOnRenderabilityChange()
  useEffect(() => {
    onRenderabilityChange({
      renderable: reason === 'renderable',
      reason: reason === 'renderable' ? null : reason,
    })
  }, [onRenderabilityChange, reason])
  return null
}

function useWidgetOnRenderabilityChange() {
  return (state: { renderable: boolean; reason: 'renderable' | 'cyclic' | 'empty' | null }) => {
    ;(globalThis as unknown as { __lastRenderability: typeof state }).__lastRenderability = state
  }
}

afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
  delete (globalThis as unknown as { __lastRenderability?: unknown }).__lastRenderability
})

describe('DependencyGraphErrorBoundary', () => {
  it('renders children normally when no render exception occurs', () => {
    render(
      <DependencyGraphErrorBoundary>
        <SafeWidget />
      </DependencyGraphErrorBoundary>,
    )
    expect(screen.getByTestId('safe-widget')).toBeInTheDocument()
  })

  it('catches a render exception thrown by a child and renders nothing (page-level banner handles messaging)', () => {
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {})
    render(
      <DependencyGraphErrorBoundary>
        <ThrowingWidget message="simulated render crash" />
      </DependencyGraphErrorBoundary>,
    )
    expect(screen.queryByTestId('safe-widget')).toBeNull()
    expect(consoleError).toHaveBeenCalled()
    expect(consoleError.mock.calls.some(call =>
      call.some(arg => arg instanceof Error && arg.message === 'simulated render crash'),
    )).toBe(true)
  })

  it('invokes the onError callback when a child throws during render', () => {
    const onError = vi.fn()
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {})
    render(
      <DependencyGraphErrorBoundary onError={onError}>
        <ThrowingWidget message="crash for onError" />
      </DependencyGraphErrorBoundary>,
    )
    expect(onError).toHaveBeenCalledTimes(1)
    expect(consoleError).toHaveBeenCalled()
  })

  it('does not invoke onError when children render successfully', () => {
    const onError = vi.fn()
    render(
      <DependencyGraphErrorBoundary onError={onError}>
        <SafeWidget />
      </DependencyGraphErrorBoundary>,
    )
    expect(onError).not.toHaveBeenCalled()
  })

  it('does not interfere with children that report renderability via callback', () => {
    const consoleError = vi.spyOn(console, 'error').mockImplementation(() => {})
    render(
      <DependencyGraphErrorBoundary>
        <RenderabilityNotifyingWidget reason="cyclic" />
      </DependencyGraphErrorBoundary>,
    )
    expect(
      (globalThis as unknown as { __lastRenderability: { reason: string | null } }).__lastRenderability.reason,
    ).toBe('cyclic')
    expect(consoleError).not.toHaveBeenCalled()
  })
})