import fs from 'node:fs'
import { readFileSync } from 'node:fs'
import { default as defaultFs, promises as fsPromises } from 'node:fs'

readFileSync('Component.tsx', 'utf8')
fs.readFileSync('App.tsx', 'utf8')
void fsPromises.readFile('Async.tsx', 'utf8')
defaultFs.readFileSync('Default.tsx', 'utf8')
;(await import('node:fs')).readFileSync('Dynamic.tsx', 'utf8')

const { readFileSync: requireReadFileSync } = require('node:fs')
const { readFileSync: createRequireReadFileSync } = createRequire(import.meta.url)('node:fs')
const { readdirSync } = require('node:fs')

requireReadFileSync('Require.tsx', 'utf8')
createRequireReadFileSync('CreateRequire.tsx', 'utf8')
readdirSync('src')
require('node:fs').readFileSync('DirectRequire.tsx', 'utf8')
