const sleep = () => new Promise<void>((done) => setTimeout(done, 1))

describe('without the timer helper', () => {
  const sleep = async () => undefined

  it('awaits the shadowed helper', async () => {
    await sleep()
  })
})
