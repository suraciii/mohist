import { expect, test } from '@playwright/test'

test('workflow session rows do not overflow a narrow panel', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 640 })
  await page.setContent(`
    <!doctype html>
    <html>
      <head>
        <style>
          * { box-sizing: border-box; }
          body { margin: 0; font-family: system-ui, sans-serif; }
          .panel { width: 320px; margin: 16px; border: 1px solid #d4d4d8; overflow-x: hidden; }
          .row { display: block; min-width: 0; padding: 8px 12px; color: #18181b; text-decoration: none; }
          .header { display: flex; flex-wrap: wrap; align-items: center; min-width: 0; column-gap: 8px; row-gap: 4px; }
          .dot { width: 14px; height: 14px; flex: 0 0 auto; border-radius: 999px; background: #22c55e; }
          .name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font: 600 12px ui-monospace, monospace; }
          .model { margin-left: auto; min-width: 0; max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; border: 1px solid #d4d4d8; padding: 2px 6px; font-size: 11px; }
          .metrics { margin-top: 4px; display: flex; flex-wrap: wrap; align-items: center; column-gap: 8px; row-gap: 2px; font-size: 11px; color: #71717a; }
          .failure { margin-top: 4px; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 11px; color: #dc2626; }
        </style>
      </head>
      <body>
        <section class="panel" data-testid="workflow-sessions-panel">
          <a class="row" data-testid="workflow-session-row" href="#">
            <div class="header" data-testid="workflow-session-row-header">
              <span class="dot" aria-hidden="true"></span>
              <span class="name">review-repair-session-with-a-very-long-custom-name-that-must-truncate</span>
              <span class="model">configured/provider-name-with-long-model -> resolved/provider-name-with-even-longer-model</span>
            </div>
            <div class="metrics" data-testid="workflow-session-row-metrics">
              <span>Failed</span>
              <span>588.4k processed</span>
              <span>93% ctx</span>
              <span>$12.34</span>
              <span>27 tools / 3 errors</span>
              <span>2m ago</span>
            </div>
            <div class="failure">probe timed out because the runner exceeded its budget and returned a long diagnostic string</div>
          </a>
        </section>
      </body>
    </html>
  `)

  const panel = page.getByTestId('workflow-sessions-panel')
  const row = page.getByTestId('workflow-session-row')
  const metrics = page.getByTestId('workflow-session-row-metrics')

  await expect(row).toBeVisible()
  await expect(row.getByText(/review-repair-session/)).toBeVisible()
  await expect(row.getByText('Failed')).toBeVisible()
  await expect(row.getByText('27 tools / 3 errors')).toBeVisible()

  await expect(panel).toHaveJSProperty('scrollWidth', await panel.evaluate((node) => node.clientWidth))
  expect(await metrics.evaluate((node) => node.getClientRects().length)).toBeGreaterThan(0)
  expect(await metrics.evaluate((node) => node.scrollWidth <= node.clientWidth)).toBe(true)
})
