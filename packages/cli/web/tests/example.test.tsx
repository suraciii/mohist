import { describe, it, expect } from 'vitest'
import { render, screen } from './test-utils'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

describe('Test environment', () => {
  it('should render a simple component', () => {
    render(<div data-testid="test-element">Test Environment Works!</div>)
    expect(screen.getByTestId('test-element')).toBeInTheDocument()
    expect(screen.getByTestId('test-element')).toHaveTextContent('Test Environment Works!')
  })

  it('should provide QueryClient wrapper', () => {
    const queryClient = new QueryClient()
    
    render(
      <QueryClientProvider client={queryClient}>
        <div>QueryClient Available</div>
      </QueryClientProvider>
    )
    
    expect(screen.getByText('QueryClient Available')).toBeInTheDocument()
  })
})
