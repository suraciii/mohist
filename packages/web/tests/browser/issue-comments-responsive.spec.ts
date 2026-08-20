import { expect, test, type Page } from '@playwright/test'

const project = {
  id: 'proj-comment-attribution',
  name: 'comment-attribution',
  repositories: [],
  createdAt: '2026-07-21T00:00:00Z',
  updatedAt: '2026-07-21T00:00:00Z',
}

const issueNumber = 454
const recordedAuthor = 'Ada Lovelace with a deliberately long declared author label'
const recordedBody = 'The recorded comment body remains readable with its matching attribution and timestamp.'

function response(data: unknown) {
  return { success: true, data }
}

function makeIssue() {
  return {
    number: issueNumber,
    title: 'Truthful comment attribution',
    body: '',
    status: 'backlog',
    workflowStage: null,
    workflowStatus: null,
    workflowRunId: null,
    health: 'active',
    projectId: project.id,
    labels: {},
    createdAt: '2026-07-21T00:00:00Z',
    updatedAt: '2026-07-21T00:00:00Z',
    comments: [
      {
        id: 'cmt-recorded',
        author: recordedAuthor,
        body: recordedBody,
        createdAt: '2026-07-21T08:30:00Z',
        attachments: [],
      },
      {
        id: 'cmt-historical',
        author: null,
        body: 'Historical comment body',
        createdAt: '2026-07-20T08:30:00Z',
        attachments: [],
      },
    ],
    attachments: [],
    priority: 'p2',
    prerequisites: [],
    isDraft: false,
    canStart: true,
    blocker: null,
    feedback: [],
  }
}

async function mockApi(page: Page) {
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url())
    const path = url.pathname.replace(/^\/api/, '')
    const method = route.request().method()

    if (method === 'GET' && path === '/projects') return route.fulfill({ json: response([project]) })
    if (method === 'GET' && path === `/projects/${project.id}/issues/${issueNumber}`)
      return route.fulfill({ json: response(makeIssue()) })
    if (method === 'GET' && path === `/projects/${project.id}/issues`) return route.fulfill({ json: response([]) })
    if (method === 'GET' && path === `/projects/${project.id}/agent/status`) {
      return route.fulfill({
        json: response({ running: false, runnerAvailable: true, activeAgents: [], capacity: { active: 0, max: 4 } }),
      })
    }
    if (method === 'GET' && path.endsWith('/diff'))
      return route.fulfill({ json: response({ available: false, message: 'No workspace' }) })
    if (method === 'GET' && path.endsWith('/commits'))
      return route.fulfill({ json: response({ available: false, message: 'No workspace' }) })
    if (method === 'GET' && path.endsWith('/workspace-status')) return route.fulfill({ json: response(null) })
    if (method === 'GET' && path === `/projects/${project.id}/workflow-profile/default`) {
      return route.fulfill({
        json: response({
          projectId: project.id,
          defaultWorkflowProfileId: 'mohist/local',
          disabledWorkflowProfileIds: [],
        }),
      })
    }
    if (method === 'GET' && path === `/projects/${project.id}/workflow-profiles`) {
      return route.fulfill({ json: response([]) })
    }
    if (method === 'GET' && path.endsWith('/opencode/models'))
      return route.fulfill({ json: response({ models: [], modelVariants: {} }) })

    return route.fulfill({
      status: 404,
      json: { success: false, error: `Unhandled browser fixture: ${method} ${path}` },
    })
  })
}

test('phone comments keep author, timestamp, and body readable without overflow', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await mockApi(page)
  await page.goto(`/${project.name}/issues/${issueNumber}`)

  const comments = page.getByTestId('issue-comment')
  await expect(comments).toHaveCount(2)
  const recorded = comments.filter({ hasText: recordedBody })
  const metadata = recorded.getByTestId('comment-metadata')
  const author = metadata.getByText(recordedAuthor, { exact: true })
  const timestamp = metadata.locator('time')
  const body = recorded.getByText(recordedBody, { exact: true })

  await expect(author).toBeVisible()
  await expect(timestamp).toBeVisible()
  await expect(timestamp).toHaveAttribute('datetime', '2026-07-21T08:30:00Z')
  await expect(body).toBeVisible()
  await expect(comments.filter({ hasText: 'Historical comment body' }).getByText('Unknown author')).toBeVisible()
  await expect(page.getByRole('textbox', { name: 'Author' })).toBeVisible()

  const [metadataBox, bodyBox] = await Promise.all([metadata.boundingBox(), body.boundingBox()])
  expect(metadataBox).not.toBeNull()
  expect(bodyBox).not.toBeNull()
  expect(metadataBox!.x).toBeGreaterThanOrEqual(0)
  expect(metadataBox!.x + metadataBox!.width).toBeLessThanOrEqual(390)
  expect(metadataBox!.y + metadataBox!.height).toBeLessThanOrEqual(bodyBox!.y)

  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  )
  expect(overflow).toBeLessThanOrEqual(0)
})
