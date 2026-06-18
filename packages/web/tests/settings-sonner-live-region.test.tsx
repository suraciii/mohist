// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { Toaster, toast } from 'sonner'
import { cleanup } from '@testing-library/react'

describe('Settings mutation toast live regions', () => {
  afterEach(() => {
    toast.dismiss()
    cleanup()
  })

  it('announces success and failure mutation feedback through sonner status regions', async () => {
    render(<Toaster />)

    toast.success('Setting updated')
    toast.error('Request failed')

    const liveRegion = screen.getByLabelText(/notifications/i)
    expect(liveRegion).toHaveAttribute('aria-live', 'polite')

    await waitFor(() => {
      expect(liveRegion).toHaveTextContent('Setting updated')
      expect(liveRegion).toHaveTextContent('Request failed')
    })
  })
})
