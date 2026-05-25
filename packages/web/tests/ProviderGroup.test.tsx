import { describe, it, expect } from 'vitest'
import { render, screen, fireEvent } from '@testing-library/react'
import { ProviderGroup } from '../src/components/ProviderGroup'

describe('ProviderGroup', () => {
  it('should render group label with count', () => {
    render(
      <ProviderGroup label="Recommended" count={3}>
        <div>item1</div>
        <div>item2</div>
        <div>item3</div>
      </ProviderGroup>
    )

    expect(screen.getByText('Recommended (3)')).toBeInTheDocument()
  })

  it('should render all children when count <= 5', () => {
    render(
      <ProviderGroup label="Test" count={3}>
        <div>item1</div>
        <div>item2</div>
        <div>item3</div>
      </ProviderGroup>
    )

    expect(screen.getByText('item1')).toBeInTheDocument()
    expect(screen.getByText('item2')).toBeInTheDocument()
    expect(screen.getByText('item3')).toBeInTheDocument()
  })

  it('should show only 5 items by default when count > 5', () => {
    const items = Array.from({ length: 8 }, (_, i) => (
      <div key={i}>item{i + 1}</div>
    ))

    render(
      <ProviderGroup label="Test" count={8}>
        {items}
      </ProviderGroup>
    )

    for (let i = 1; i <= 5; i++) {
      expect(screen.getByText(`item${i}`)).toBeInTheDocument()
    }
    expect(screen.queryByText('item6')).not.toBeInTheDocument()
    expect(screen.queryByText('item7')).not.toBeInTheDocument()
    expect(screen.queryByText('item8')).not.toBeInTheDocument()
  })

  it('should show toggle button when count > 5', () => {
    render(
      <ProviderGroup label="Test" count={8}>
        {Array.from({ length: 8 }, (_, i) => <div key={i}>item{i + 1}</div>)}
      </ProviderGroup>
    )

    expect(screen.getByText('Show all (8)')).toBeInTheDocument()
  })

  it('should not show toggle button when count <= 5', () => {
    render(
      <ProviderGroup label="Test" count={3}>
        <div>item1</div>
        <div>item2</div>
        <div>item3</div>
      </ProviderGroup>
    )

    expect(screen.queryByText(/Show all/)).not.toBeInTheDocument()
  })

  it('should toggle expand/collapse on button click', () => {
    render(
      <ProviderGroup label="Test" count={8}>
        {Array.from({ length: 8 }, (_, i) => <div key={i}>item{i + 1}</div>)}
      </ProviderGroup>
    )

    expect(screen.queryByText('item8')).not.toBeInTheDocument()

    fireEvent.click(screen.getByText('Show all (8)'))

    expect(screen.getByText('item8')).toBeInTheDocument()
    expect(screen.getByText('Show less')).toBeInTheDocument()

    fireEvent.click(screen.getByText('Show less'))

    expect(screen.queryByText('item8')).not.toBeInTheDocument()
  })

  it('should render nothing when count is 0', () => {
    const { container } = render(
      <ProviderGroup label="Empty" count={0}>
        <div>item</div>
      </ProviderGroup>
    )

    expect(container.innerHTML).toBe('')
  })

  it('should force expanded when expanded prop is true', () => {
    render(
      <ProviderGroup label="Test" count={8} expanded={true}>
        {Array.from({ length: 8 }, (_, i) => <div key={i}>item{i + 1}</div>)}
      </ProviderGroup>
    )

    expect(screen.getByText('item8')).toBeInTheDocument()
  })

  it('should render actions slot when provided', () => {
    render(
      <ProviderGroup label="Custom" count={2} actions={<button>Add Custom</button>}>
        <div>item1</div>
        <div>item2</div>
      </ProviderGroup>
    )

    expect(screen.getByText('Add Custom')).toBeInTheDocument()
  })
})
