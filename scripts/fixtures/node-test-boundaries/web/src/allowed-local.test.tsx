const element = document.createElement('div')

Object.defineProperty(element, 'scrollHeight', { configurable: true, value: 24 })
Reflect.defineProperty(element, 'clientHeight', { configurable: true, value: 12 })
Reflect.deleteProperty(element, 'unused')
vi.spyOn(window, 'matchMedia')
vi.stubGlobal('exampleBoundary', true)
