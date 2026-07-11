const sleep = (resolve: () => void) => setTimeout(resolve, 1)

it('waits for a timer through a named Promise executor', async () => {
  await new Promise<void>(sleep)
})
