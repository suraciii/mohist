import '../scripts/write-build-info.mjs'
import '../src/cli.js'

it('imports the executable build script', async () => {
  await import('../scripts/write-build-info.mjs')
  await import('../src/cli.js')
})
