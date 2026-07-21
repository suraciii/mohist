import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { ArtifactTextContent } from './ArtifactTextContent'

describe('ArtifactTextContent', () => {
  it('keeps non-markdown content raw and wrapped', () => {
    const content = 'a'.repeat(300)
    render(<ArtifactTextContent content={content} contentType="application/json" />)
    expect(screen.getByText(content)).toHaveClass('whitespace-pre-wrap')
    expect(screen.getByText(content)).toHaveClass('[overflow-wrap:anywhere]')
  })

  it('uses MarkdownReader for markdown content', () => {
    render(<ArtifactTextContent content="# Proposal" contentType="text/markdown" />)
    expect(screen.getByRole('heading', { name: 'Proposal' })).toBeInTheDocument()
  })
})
