import assert from 'node:assert/strict'
import { test } from 'node:test'

import {
  parseAssemblyName,
  parseTargetFramework,
  resolveApphostPath,
  resolveDiscoveryCommand,
  resolveFocusedCommand,
} from './focused.js'

const CSPROJ = `
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <AssemblyName>Mohist.Server.Tests</AssemblyName>
  </PropertyGroup>
</Project>`

test('parseTargetFramework and parseAssemblyName read csproj properties', () => {
  assert.equal(parseTargetFramework(CSPROJ), 'net11.0')
  assert.equal(parseAssemblyName(CSPROJ), 'Mohist.Server.Tests')
  assert.equal(parseTargetFramework('<Project></Project>'), undefined)
})

test('parseTargetFramework takes the first TargetFrameworks entry', () => {
  assert.equal(parseTargetFramework('<TargetFrameworks>net11.0;net8.0</TargetFrameworks>'), 'net11.0')
})

test('resolveApphostPath points at the compiled apphost next to the assembly', () => {
  const path = resolveApphostPath({ csprojXml: CSPROJ })
  assert.equal(path, 'bin/Debug/net11.0/Mohist.Server.Tests')
  const withDir = resolveApphostPath({
    csprojXml: CSPROJ,
    projectDir: 'packages/server/tests/Mohist.Server.Tests',
    configuration: 'Release',
  })
  assert.equal(withDir, 'packages/server/tests/Mohist.Server.Tests/bin/Release/net11.0/Mohist.Server.Tests')
})

test('resolveFocusedCommand emits apphost -class, never dotnet --filter', () => {
  const cmd = resolveFocusedCommand({ csprojXml: CSPROJ, className: 'Mohist.Server.Tests.Api.Foo' })
  assert.ok(cmd.args.includes('-class'))
  assert.ok(cmd.args.includes('Mohist.Server.Tests.Api.Foo'))
  assert.ok(cmd.args.includes('-noColor'))
  assert.ok(cmd.args.includes('-noLogo'))
  assert.equal(
    cmd.args.some((a) => a.includes('--filter')),
    false,
  )
  assert.equal(cmd.apphost.includes('dotnet test'), false)
  assert.deepEqual(cmd.verify, ['-list', 'classes', '-noColor', '-noLogo'])
  const reported = cmd.report('reports/foo.trx')
  assert.deepEqual(reported.slice(-2), ['-trx', 'reports/foo.trx'])
  assert.equal(
    reported.some((a) => a === '-class'),
    true,
  )
})

test('resolveDiscoveryCommand uses the compiled apphost and emits a nonzero-list request', () => {
  const cmd = resolveDiscoveryCommand({ csprojXml: CSPROJ })
  assert.equal(cmd.apphost, 'bin/Debug/net11.0/Mohist.Server.Tests')
  assert.deepEqual(cmd.args, ['-list', 'full/json', '-preEnumerateTheories', '-noColor', '-noLogo'])
  assert.equal(cmd.args.includes('--filter'), false)
})
