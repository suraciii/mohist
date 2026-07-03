// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { InsightsRangeSelector } from './InsightsRangeSelector'
import type { InsightsRange } from '../model/insights-range'

afterEach(() => {
  cleanup()
})

describe('InsightsRangeSelector', () => {
  it('renders exactly three preset buttons (7d / 30d / 90d) with no from/to picker', () => {
    render(<InsightsRangeSelector value="30d" onChange={() => {}} />)

    expect(screen.getByTestId('insights-range-option-7d')).toBeInTheDocument()
    expect(screen.getByTestId('insights-range-option-30d')).toBeInTheDocument()
    expect(screen.getByTestId('insights-range-option-90d')).toBeInTheDocument()

    const selector = screen.getByTestId('insights-range-selector')
    expect(selector.querySelectorAll('button')).toHaveLength(3)
    expect(selector.querySelector('input[type="date"]')).toBeNull()
  })

  it('marks only the active preset with aria-pressed=true and data-active=true', () => {
    render(<InsightsRangeSelector value="7d" onChange={() => {}} />)

    const option7d = screen.getByTestId('insights-range-option-7d')
    const option30d = screen.getByTestId('insights-range-option-30d')
    const option90d = screen.getByTestId('insights-range-option-90d')

    expect(option7d.getAttribute('aria-pressed')).toBe('true')
    expect(option7d.getAttribute('data-active')).toBe('true')
    expect(option30d.getAttribute('aria-pressed')).toBe('false')
    expect(option30d.getAttribute('data-active')).toBe('false')
    expect(option90d.getAttribute('aria-pressed')).toBe('false')
    expect(option90d.getAttribute('data-active')).toBe('false')
  })

  it('invokes onChange with the selected range when a preset is clicked', () => {
    const onChange = vi.fn<(next: InsightsRange) => void>()
    render(<InsightsRangeSelector value="30d" onChange={onChange} />)

    fireEvent.click(screen.getByTestId('insights-range-option-90d'))
    expect(onChange).toHaveBeenCalledWith('90d')

    fireEvent.click(screen.getByTestId('insights-range-option-7d'))
    expect(onChange).toHaveBeenCalledWith('7d')
  })
})