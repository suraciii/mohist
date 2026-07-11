it('does not use jsdom to assert page-fit geometry', () => {
  const element = document.createElement('div')
  const viewportWidth = window.innerWidth
  const rect = element.getBoundingClientRect()
  const scrollWidth = element.scrollWidth
  const clientWidth = element.clientWidth
  const offsetWidth = element.offsetWidth
  const { scrollWidth: destructuredScrollWidth, clientWidth: destructuredClientWidth } = element
  const { innerWidth } = window
  const { right } = element.getBoundingClientRect()

  expect(element.scrollWidth).toBeLessThanOrEqual(element.clientWidth)
  expect(scrollWidth).toBeLessThanOrEqual(clientWidth)
  expect(destructuredScrollWidth).toBeLessThanOrEqual(destructuredClientWidth)
  expect(element.offsetWidth).toBeLessThanOrEqual(window.innerWidth)
  expect(offsetWidth).toBeLessThanOrEqual(viewportWidth)
  expect(offsetWidth).toBeLessThanOrEqual(innerWidth)
  expect(rect.right).toBeLessThanOrEqual(viewportWidth)
  expect(right).toBeLessThanOrEqual(viewportWidth)
  expect(element.getBoundingClientRect().bottom).toBeLessThanOrEqual(visualViewport!.height)

  if (element.scrollWidth > element.clientWidth) throw new Error('jsdom does not measure page overflow')
})
