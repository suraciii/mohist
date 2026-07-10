Element['prototype'].scrollTo = () => {}
Object['defineProperty']((HTMLElement as typeof HTMLElement)['prototype'], 'scrollHeight', { configurable: true, value: 1 })
Reflect['defineProperty'](document['documentElement'], 'scrollWidth', { configurable: true, value: 1 })
Reflect.deleteProperty((globalThis as typeof globalThis)['navigator']!, 'clipboard')
