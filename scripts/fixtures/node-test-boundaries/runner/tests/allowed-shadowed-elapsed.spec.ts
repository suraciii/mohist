const elapsed = Date.now() - Date.now()

describe('without the elapsed value', () => {
  const elapsed = 0

  it('asserts the shadowed value', () => {
    expect(elapsed).toBe(0)
  })
})
