it.skip('does not run', () => {})
test.only.each([1])('runs only %i', () => {})
describe.todo('is not implemented')
describe.skipIf(process.platform === 'win32')('cannot skip a suite', () => {})
