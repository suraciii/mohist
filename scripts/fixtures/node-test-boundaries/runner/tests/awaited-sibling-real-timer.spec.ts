describe('fake timer suite', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  it('does not wait for a timer', () => {})
})

describe('real timer suite', () => {
  it('waits for a real timer', async () => {
    await new Promise<void>((done) => setTimeout(done, 1))
  })
})
