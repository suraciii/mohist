vi.mock('../src/system/process-policy.js', () => ({}))
vi.doMock(import('../src/system/process-policy.js'), () => ({}))
vi.mock('../src/system/process.js', () => ({}))

it('mocks the external process policy', () => {})
