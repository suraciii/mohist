it('drives a timer with fake time', async () => {
  vi.useFakeTimers()
  const complete = new Promise<void>((resolve) => setTimeout(resolve, 10))

  await vi.advanceTimersByTimeAsync(10)
  await complete
})
