it.skip('does not run', () => {})
test.only.each([1])('runs only %i', () => {})
describe.todo('is not implemented')
it.skipIf(process.platform === 'win32')('does not run on Windows', () => {})
