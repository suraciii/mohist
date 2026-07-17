### Requirement: A block cursor renders at the end of streaming assistant text in a live session

While the hosting session is alive (running) and an assistant text part is incomplete or actively streaming, a block cursor SHALL be rendered at the end of the part's streamed text. The block cursor is the in-stream visual indicator that the assistant is still writing at that location. The block cursor SHALL be gated on session liveness: it SHALL render only while the session is running and the text part is open (no `completedAt`, or marked as actively streaming).

#### Scenario: Streaming text part shows a block cursor in a live session
- **WHEN** an assistant text part is incomplete or actively streaming and the session is running
- **THEN** a block cursor SHALL be rendered at the end of the streamed text

#### Scenario: Block cursor is gated on session liveness
- **WHEN** the session is not running (ended, completed, failed, cancelled, or inactive) and a text part is incomplete
- **THEN** the block cursor SHALL NOT render

### Requirement: The block cursor is removed when the text part completes or the session ends

When an assistant text part transitions to complete (its `completedAt` is set), the block cursor at the end of that text SHALL be removed. When the hosting session ends while a text part is still open, the block cursor SHALL also be removed.

#### Scenario: Block cursor is removed on text part completion
- **WHEN** an assistant text part that was streaming transitions to completed
- **THEN** the block cursor SHALL no longer be rendered at the end of that text

#### Scenario: Block cursor is removed when the session ends mid-stream
- **WHEN** a running session that is streaming an assistant text part transitions to not running
- **THEN** the block cursor SHALL be removed from that text part

### Requirement: The block cursor is decorative and hidden from assistive technology

The block cursor is a decorative visual cue and SHALL be hidden from assistive technology. It SHALL NOT be exposed as a focusable element, SHALL NOT carry a semantic role, and SHALL be marked as ignored by screen readers (for example via `aria-hidden`).

#### Scenario: Block cursor is hidden from screen readers
- **WHEN** the block cursor renders
- **THEN** it SHALL be marked as hidden from assistive technology
- **AND** SHALL NOT be focusable and SHALL NOT carry a semantic role
