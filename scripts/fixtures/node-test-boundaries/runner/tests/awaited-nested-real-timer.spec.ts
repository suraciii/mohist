describe('outer fake timer suite', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  describe('inner real timer suite', () => {
    beforeEach(() => {
      vi.useRealTimers()
    })

    it('waits for a real timer', async () => {
      await new Promise<void>((done) => setTimeout(done, 1))
    })
  })
})
