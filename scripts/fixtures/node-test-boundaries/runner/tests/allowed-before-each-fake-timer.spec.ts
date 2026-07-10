beforeEach(() => {
  vi.useFakeTimers()
})

it('drives a timer with fake time configured by a hook', async () => {
  const complete = new Promise<void>((done) => setTimeout(done, 10))

  await vi.advanceTimersByTimeAsync(10)
  await complete
})
