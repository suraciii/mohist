import '@testing-library/jest-dom'
import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { TodoContentView } from './todo-view'

describe('TodoContentView', () => {
  it('renders counts and individual todos', () => {
    render(
      <TodoContentView
        input={JSON.stringify({
          todos: [
            { status: 'completed', content: 'A' },
            { status: 'completed', content: 'B' },
            { status: 'in_progress', content: 'C' },
            { status: 'pending', content: 'D' },
          ],
        })}
      />,
    )

    expect(screen.getByText('2/4 completed')).toBeInTheDocument()
    expect(screen.getByText('1 in progress')).toBeInTheDocument()
    expect(screen.getByText('1 pending')).toBeInTheDocument()
    expect(screen.getByText('A')).toBeInTheDocument()
    expect(screen.getByText('C')).toBeInTheDocument()
    expect(screen.getByText('D')).toBeInTheDocument()
  })

  it('renders nothing when input is missing or empty', () => {
    const { container } = render(<TodoContentView />)

    expect(container).toBeEmptyDOMElement()
  })

  it('renders nothing when todos is empty', () => {
    const { container } = render(
      <TodoContentView input={JSON.stringify({ todos: [] })} />,
    )

    expect(container).toBeEmptyDOMElement()
  })

  it('truncates display to 8 items and shows "more" indicator', () => {
    const todos = Array.from({ length: 12 }, (_, i) => ({
      status: 'pending',
      content: `task ${i + 1}`,
    }))

    const { container } = render(
      <TodoContentView input={JSON.stringify({ todos })} />,
    )

    expect(screen.getByText('0/12 completed')).toBeInTheDocument()
    expect(screen.getByText('12 pending')).toBeInTheDocument()
    expect(screen.getByText('task 1')).toBeInTheDocument()
    expect(screen.getByText('task 8')).toBeInTheDocument()
    expect(screen.queryByText('task 9')).not.toBeInTheDocument()
    expect(container.textContent).toContain('...and 4 more')
  })
})
