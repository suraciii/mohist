it('allows vi.waitFor outside a RunnerHost spec', async () => {
  await vi.waitFor(() => expect(true).toBe(true))
})
