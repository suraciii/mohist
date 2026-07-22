import { brotliCompressSync, gzipSync } from 'node:zlib'
import { readdir, readFile, stat } from 'node:fs/promises'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const repoRoot = join(fileURLToPath(new URL('.', import.meta.url)), '..')
const distRoot = join(repoRoot, 'packages/web/dist')
const indexPath = join(distRoot, 'index.html')
const index = await readFile(indexPath, 'utf8')
const assetRoot = join(distRoot, 'assets')

const assetNames = await readdir(assetRoot)
const assetFiles = []
for (const name of assetNames) {
  const path = join(assetRoot, name)
  if ((await stat(path)).isFile()) assetFiles.push({ name, path, bytes: await readFile(path) })
}

if (assetFiles.length === 0) throw new Error('web build contains no assets')
if (assetFiles.some(({ name }) => name.endsWith('.map'))) throw new Error('web build must not emit source maps')

const fingerprint = /-[A-Za-z0-9_-]{8,}\.[A-Za-z0-9]+$/
for (const { name } of assetFiles) {
  if (!fingerprint.test(name)) throw new Error(`asset is not fingerprinted: ${name}`)
}

const initialNames = [...index.matchAll(/(?:src|href)="\/assets\/([^"]+)"/g)].map((match) => match[1])
if (initialNames.length === 0) throw new Error('index.html references no initial assets')

const byName = new Map(assetFiles.map((asset) => [asset.name, asset]))
const initialAssets = initialNames.map((name) => {
  const asset = byName.get(name)
  if (!asset) throw new Error(`index.html references missing asset: ${name}`)
  return asset
})

const compressed = (buffer) => ({
  brotli: brotliCompressSync(buffer).byteLength,
  gzip: gzipSync(buffer).byteLength,
})

const initialRaw = initialAssets.reduce((sum, asset) => sum + asset.bytes.byteLength, 0)
const initialBrotli = initialAssets.reduce((sum, asset) => sum + compressed(asset.bytes).brotli, 0)
const initialGzip = initialAssets.reduce((sum, asset) => sum + compressed(asset.bytes).gzip, 0)
const routeAssets = assetFiles.filter(({ name }) => name.endsWith('.js') && !initialNames.includes(name))
if (routeAssets.length === 0) throw new Error('web build contains no route JavaScript chunks')
const largestRoute = routeAssets.reduce((largest, asset) => {
  const sizes = compressed(asset.bytes)
  return sizes.brotli > largest.brotli ? { name: asset.name, ...sizes } : largest
}, { name: 'none', brotli: 0, gzip: 0 })

console.log(JSON.stringify({
  initial: { raw: initialRaw, brotli: initialBrotli, gzip: initialGzip, assets: initialNames },
  routeJsLargest: largestRoute,
  assetCount: assetFiles.length,
}, null, 2))

const initialBudget = 1_200_000
const routeBudget = 750_000
if (initialBrotli > initialBudget || initialGzip > initialBudget) {
  throw new Error(`initial compressed transfer exceeds ${initialBudget} bytes`)
}
if (largestRoute.brotli > routeBudget || largestRoute.gzip > routeBudget) {
  throw new Error(`route JavaScript exceeds ${routeBudget} bytes: ${largestRoute.name}`)
}
