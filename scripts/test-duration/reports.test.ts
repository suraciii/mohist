import assert from 'node:assert/strict'
import { test } from 'node:test'

import { parseReport, parseTrx, parseVitestJson } from './reports.js'

const TRX = `<?xml version="1.0"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="Ns.ATests.Passes_Fast" outcome="Passed" testId="1" duration="00:00:00.0020000" />
    <UnitTestResult testName="Ns.ATests.Fails_Slow" outcome="Failed" testId="2" duration="00:00:01.5000000" />
    <UnitTestResult testName="Ns.ATests.Skipped" outcome="NotExecuted" testId="3" duration="00:00:00.0000000" />
  </Results>
</TestRun>`

test('parseTrx reads testName, outcome and HH:MM:SS.fffffff duration', () => {
  const cases = parseTrx(TRX)
  assert.equal(cases.length, 3)
  const byName = new Map(cases.map((c) => [c.name, c]))
  assert.equal(byName.get('Ns.ATests.Passes_Fast')!.durationMs, 2)
  assert.equal(byName.get('Ns.ATests.Fails_Slow')!.durationMs, 1500)
  assert.equal(byName.get('Ns.ATests.Passes_Fast')!.outcome, 'passed')
  assert.equal(byName.get('Ns.ATests.Fails_Slow')!.outcome, 'failed')
  assert.equal(byName.get('Ns.ATests.Skipped')!.outcome, 'not-run')
})

test('parseTrx keeps skipped, not-run, and error outcomes distinct', () => {
  const cases = parseTrx(`<TestRun><Results>
    <UnitTestResult testName="Ns.Skip" outcome="Skipped" duration="00:00:00.0000000" />
    <UnitTestResult testName="Ns.NotRun" outcome="NotRun" duration="00:00:00.0000000" />
    <UnitTestResult testName="Ns.Error" outcome="Error" duration="00:00:00.1000000" />
  </Results></TestRun>`)
  assert.deepEqual(
    cases.map((case_) => case_.outcome),
    ['skipped', 'not-run', 'error'],
  )
})

test('parseTrx ignores UnitTest definitions and only reads UnitTestResult', () => {
  const withDefinition = `<TestRun><TestDefinitions>
    <UnitTest name="x" id="1"><TestMethod className="Ns.A"/></UnitTest>
  </TestDefinitions>
  <Results><UnitTestResult testName="Ns.A.P" outcome="Passed" duration="00:00:00.0100000"/></Results></TestRun>`
  const cases = parseTrx(withDefinition)
  assert.deepEqual(
    cases.map((c) => c.name),
    ['Ns.A.P'],
  )
})

test('parseTrx parses result blocks that carry child elements (failed test output)', () => {
  const block = `<UnitTestResult testName="Ns.A.Boom" outcome="Failed" duration="00:00:00.3000000">
      <Output><ErrorInfo><Message>boom</Message></ErrorInfo></Output>
    </UnitTestResult>`
  const cases = parseTrx(`<TestRun><Results>${block}</Results></TestRun>`)
  assert.equal(cases.length, 1)
  assert.equal(cases[0].durationMs, 300)
  assert.equal(cases[0].outcome, 'failed')
})

test('parseTrx decodes XML entities in parameterized test display names', () => {
  const xml =
    '<TestRun><Results><UnitTestResult testName="Ns.Theory(value: \\&quot;a &amp; b\\&quot;, symbol: &#x3C;)" outcome="Passed" duration="00:00:00.0010000"/></Results></TestRun>'
  assert.equal(parseTrx(xml)[0].name, 'Ns.Theory(value: "a & b", symbol: <)')
})

test('parseTrx removes the extra TRX escaping layer from string arguments', () => {
  const xml = String.raw`<TestRun><Results><UnitTestResult testName="Ns.Theory(value: \&quot;line\\nquote: \\\&quot;x\\\&quot;\&quot;)" outcome="Passed" duration="00:00:00.0010000"/></Results></TestRun>`
  assert.equal(parseTrx(xml)[0].name, 'Ns.Theory(value: "line\\nquote: \\"x\\"")')
})

const VITEST = JSON.stringify({
  numTotalTests: 2,
  testResults: [
    {
      name: '/repo/src/a.test.ts',
      startTime: 1000,
      endTime: 1125,
      status: 'passed',
      assertionResults: [
        { fullName: 'suite a is fast', title: 'is fast', status: 'passed', duration: 1.5 },
        { fullName: 'suite a is slow', title: 'is slow', status: 'failed', duration: 250 },
      ],
    },
  ],
})

test('parseVitestJson reads fullName, status and duration in milliseconds', () => {
  const cases = parseVitestJson(VITEST)
  assert.equal(cases.length, 2)
  const slow = cases.find((c) => c.name === 'suite a is slow')!
  assert.equal(slow.durationMs, 250)
  assert.equal(slow.outcome, 'failed')
  assert.equal(slow.file, '/repo/src/a.test.ts')
  const fast = cases.find((c) => c.name === 'suite a is fast')!
  assert.equal(fast.outcome, 'passed')
})

test('parseVitestJson reconstructs fullName from ancestorTitles when missing', () => {
  const cases = parseVitestJson(
    JSON.stringify({
      testResults: [
        {
          name: '/repo/b.test.ts',
          assertionResults: [{ title: 'works', ancestorTitles: ['suite b'], status: 'passed' }],
        },
      ],
    }),
  )
  assert.equal(cases[0].name, 'suite b works')
  assert.equal(cases[0].durationMs, 0)
})

test('parseReport dispatches by format', () => {
  assert.equal(parseReport('trx', TRX).length, 3)
  assert.equal(parseReport('vitest', VITEST).length, 2)
})
