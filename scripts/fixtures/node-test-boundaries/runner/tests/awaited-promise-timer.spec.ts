it('waits for an inline timer promise', async () => {
  await new Promise<void>((done) => setTimeout(done, 1))
})
