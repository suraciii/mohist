// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import { ChartContainer } from './ChartContainer'
import type { ChartStatus } from './ChartContainer'

function renderContainer(status: ChartStatus, emptyAction: React.ReactNode = <p>No data</p>) {
  return render(
    <ChartContainer status={status} emptyAction={emptyAction}>
      <div data-testid="chart-content">Chart content</div>
    </ChartContainer>,
  )
}

describe('ChartContainer', () => {
  afterEach(() => {
    cleanup()
  })

  it('renders loading state with role=status and aria-live=polite', () => {
    renderContainer('loading')

    const loading = screen.getByTestId('chart-container-loading')
    expect(loading).toBeInTheDocument()
    expect(loading).toHaveAttribute('role', 'status')
    expect(loading).toHaveAttribute('aria-live', 'polite')

    expect(screen.queryByTestId('chart-container-error')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-empty')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-content')).not.toBeInTheDocument()
  })

  it('renders loading state with custom message', () => {
    render(
      <ChartContainer status="loading" emptyAction={null} loadingMessage="Custom loading">
        <div data-testid="chart-content">Content</div>
      </ChartContainer>,
    )

    expect(screen.getByTestId('chart-container-loading')).toHaveTextContent('Custom loading')
  })

  it('renders error state with role=status and aria-live=polite', () => {
    renderContainer('error')

    const error = screen.getByTestId('chart-container-error')
    expect(error).toBeInTheDocument()
    expect(error).toHaveAttribute('role', 'status')
    expect(error).toHaveAttribute('aria-live', 'polite')

    expect(screen.queryByTestId('chart-container-loading')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-empty')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-content')).not.toBeInTheDocument()
  })

  it('renders error state with custom message', () => {
    render(
      <ChartContainer status="error" emptyAction={null} errorMessage="Custom error">
        <div data-testid="chart-content">Content</div>
      </ChartContainer>,
    )

    expect(screen.getByTestId('chart-container-error')).toHaveTextContent('Custom error')
  })

  it('renders empty state with caller-supplied concrete next action', () => {
    const emptyAction = <p data-testid="empty-action">Data appears once usage is reported</p>
    renderContainer('empty', emptyAction)

    const empty = screen.getByTestId('chart-container-empty')
    expect(empty).toBeInTheDocument()
    expect(screen.getByTestId('empty-action')).toBeInTheDocument()
    expect(screen.getByTestId('empty-action')).toHaveTextContent('Data appears once usage is reported')

    expect(screen.queryByTestId('chart-container-loading')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-error')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-content')).not.toBeInTheDocument()
  })

  it('renders resolved state with children', () => {
    renderContainer('resolved')

    expect(screen.getByTestId('chart-content')).toBeInTheDocument()
    expect(screen.getByText('Chart content')).toBeInTheDocument()

    expect(screen.queryByTestId('chart-container-loading')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-error')).not.toBeInTheDocument()
    expect(screen.queryByTestId('chart-container-empty')).not.toBeInTheDocument()
  })

  it('loading state renders exclusively (no other state branch visible)', () => {
    const { container } = render(
      <ChartContainer status="loading" emptyAction={<p>No data</p>}>
        <div data-testid="chart-content" />
      </ChartContainer>,
    )

    expect(container.querySelector('[data-testid="chart-container-loading"]')).toBeInTheDocument()
    expect(container.querySelector('[data-testid="chart-container-error"]')).toBeNull()
    expect(container.querySelector('[data-testid="chart-container-empty"]')).toBeNull()
    expect(container.querySelector('[data-testid="chart-content"]')).toBeNull()
  })

  it('error state renders exclusively (no other state branch visible)', () => {
    const { container } = render(
      <ChartContainer status="error" emptyAction={<p>No data</p>}>
        <div data-testid="chart-content" />
      </ChartContainer>,
    )

    expect(container.querySelector('[data-testid="chart-container-error"]')).toBeInTheDocument()
    expect(container.querySelector('[data-testid="chart-container-loading"]')).toBeNull()
    expect(container.querySelector('[data-testid="chart-container-empty"]')).toBeNull()
    expect(container.querySelector('[data-testid="chart-content"]')).toBeNull()
  })

  it('empty state renders exclusively (no other state branch visible)', () => {
    const { container } = render(
      <ChartContainer status="empty" emptyAction={<p data-testid="empty-action">No data</p>}>
        <div data-testid="chart-content" />
      </ChartContainer>,
    )

    expect(container.querySelector('[data-testid="chart-container-empty"]')).toBeInTheDocument()
    expect(container.querySelector('[data-testid="chart-container-loading"]')).toBeNull()
    expect(container.querySelector('[data-testid="chart-container-error"]')).toBeNull()
    expect(container.querySelector('[data-testid="chart-content"]')).toBeNull()
  })

  it('resolved state renders exclusively (no other state branch visible)', () => {
    const { container } = render(
      <ChartContainer status="resolved" emptyAction={<p>No data</p>}>
        <div data-testid="chart-content" />
      </ChartContainer>,
    )

    expect(container.querySelector('[data-testid="chart-content"]')).toBeInTheDocument()
    expect(container.querySelector('[data-testid="chart-container-loading"]')).toBeNull()
    expect(container.querySelector('[data-testid="chart-container-error"]')).toBeNull()
    expect(container.querySelector('[data-testid="chart-container-empty"]')).toBeNull()
  })
})
