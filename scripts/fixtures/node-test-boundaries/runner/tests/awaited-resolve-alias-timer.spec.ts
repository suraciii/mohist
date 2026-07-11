it('waits for a timer through a Promise resolver alias', async () => {
  await new Promise<void>((resolve) => {
    const done = resolve
    setTimeout(done, 1)
  })
})
