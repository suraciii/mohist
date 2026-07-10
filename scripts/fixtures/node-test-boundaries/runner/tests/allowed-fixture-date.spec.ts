it('uses a date only to build fixture data', () => {
  const fixture = {
    expiresAt: new Date(Date.now() + 60_000).toISOString(),
  }

  setTimeout(() => undefined, 1)
  expect(fixture.expiresAt).toBeTypeOf('string')
})
