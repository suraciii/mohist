it('asserts elapsed wall-clock time', () => {
  const startedAt = Date.now()
  expect(Date.now() - startedAt).toBeLessThan(10)
  expect(performance.now() - startedAt).toBeLessThan(10)
})
