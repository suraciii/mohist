import { describe, expect, it } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import { IssueDescriptionSection } from './IssueDescriptionSection'

const resolveIssueAttachment = () => null

describe('IssueDescriptionSection', () => {
  it('renders only the provided description content', () => {
    render(
      <IssueDescriptionSection
        description={'## Visible description\n\nOnly this content is readable.'}
        resolveIssueAttachment={resolveIssueAttachment}
      />,
    )

    const section = screen.getByTestId('description-section')
    expect(within(section).getByText('Visible description')).toBeTruthy()
    expect(within(section).queryByText('recommended_workflow')).toBeNull()
  })

  it('builds its collapsed hint from description content only', () => {
    const description = `Description begins here ${'useful context '.repeat(35)}`
    render(
      <IssueDescriptionSection
        description={description}
        resolveIssueAttachment={resolveIssueAttachment}
      />,
    )

    const hint = screen.getByTestId('description-preview-hint')
    expect(hint).toHaveTextContent('Description begins here')
    expect(hint).not.toHaveTextContent('recommended_workflow')
    expect(hint).not.toHaveTextContent('---')
  })

  it('renders no Description section for empty content', () => {
    const { container } = render(
      <IssueDescriptionSection description={'\n\n'} resolveIssueAttachment={resolveIssueAttachment} />,
    )

    expect(container).toBeEmptyDOMElement()
  })
})
