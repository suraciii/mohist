const sleep = () => {
  const completion = new Promise<void>((done) => setTimeout(done, 1))
  return completion
}

it('waits for a locally bound timer promise', async () => {
  await sleep()
})
