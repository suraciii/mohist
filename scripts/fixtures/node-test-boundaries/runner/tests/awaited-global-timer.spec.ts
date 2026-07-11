it('waits for a timer through the Node global object', async () => {
  await new Promise<void>((resolve) => global.setTimeout(resolve, 1))
})
