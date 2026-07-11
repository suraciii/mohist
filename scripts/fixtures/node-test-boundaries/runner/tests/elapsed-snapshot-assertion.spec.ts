it('asserts elapsed wall-clock time from two snapshots', () => {
  const started = Date.now()
  const ended = Date.now()

  expect(ended - started).toBeLessThan(2)
})
