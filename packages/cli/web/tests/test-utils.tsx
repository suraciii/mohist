import React, { type ReactElement, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter } from 'react-router-dom'
import { render, type RenderOptions } from '@testing-library/react'

function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
      mutations: {
        retry: false,
      },
    },
  })
}

interface WrapperProps {
  children: ReactNode
}

function QueryClientWrapper({ children }: WrapperProps) {
  const [queryClient] = React.useState(() => createQueryClient())

  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>{children}</BrowserRouter>
    </QueryClientProvider>
  )
}

interface CustomRenderOptions extends Omit<RenderOptions, 'wrapper'> {
  wrapper?: React.ComponentType<WrapperProps>
}

function customRender(
  ui: ReactElement,
  { wrapper: WrapperComponent = QueryClientWrapper, ...options }: CustomRenderOptions = {}
) {
  return render(ui, { wrapper: WrapperComponent, ...options })
}

export * from '@testing-library/react'
export { render as baseRender, customRender as render, QueryClientWrapper, createQueryClient }
