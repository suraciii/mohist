it('asserts elapsed wall-clock time through a local binding', () => {
  const startedAt = Date.now()
  const elapsed = Date.now() - startedAt

  expect(elapsed).toBeLessThan(10)
})
