const sleep = () => new Promise<void>((resolve) => setTimeout(resolve, 1))

it('waits for a local timer helper', async () => {
  await sleep()
})
