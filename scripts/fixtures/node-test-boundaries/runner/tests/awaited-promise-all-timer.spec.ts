it('waits for a timer promise through Promise.all', async () => {
  await Promise.all([new Promise<void>((done) => setTimeout(done, 1))])
})
