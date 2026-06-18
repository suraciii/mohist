## ADDED Requirements

### Requirement: REQ-ATT-001 Attachment domain model and ownership

An Attachment SHALL be a user-uploaded file recorded with a stable id, the original filename, a content type, a byte size, and a server-generated stored content location. An Attachment SHALL be owned by exactly one attachable owner: an issue body or an issue comment. An Attachment SHALL be user-authored content and SHALL be distinct from workflow/task artifacts (agent-produced task outputs), which remain on their existing internal path. Attachments SHALL NOT be supported on chat or any surface other than issue bodies and issue comments in this capability.

#### Scenario: Attachment record carries the required fields

- **WHEN** a file is uploaded as an attachment
- **THEN** the resulting Attachment record SHALL expose a stable id, the original filename, a content type, a byte size, and a server-generated stored content location

#### Scenario: Attachment is owned by an issue body or a comment

- **WHEN** an attachment is bound to an owner
- **THEN** the owner SHALL be exactly one issue body or one issue comment
- **AND** the attachment SHALL NOT be bound to more than one owner

#### Scenario: Attachments are distinct from workflow artifacts

- **WHEN** a workflow/task produces an artifact output
- **THEN** that artifact SHALL NOT be represented as an Attachment in this capability
- **AND** the existing internal artifact-upload path SHALL remain unchanged

### Requirement: REQ-ATT-002 Upload produces a stable pending reference before ownership

Uploading a file SHALL produce a stable attachment reference that is not yet bound to any owner. Attaching SHALL bind an existing pending reference to an issue or comment owner. This decouples the compose UX (paste or drag mid-edit) from ownership and mirrors the existing artifact upload-then-bind pattern.

#### Scenario: Upload returns a stable reference before an owner is chosen

- **WHEN** a user uploads a file from the issue body editor or a comment composer before the issue or comment is saved
- **THEN** the upload SHALL return a stable attachment id
- **AND** the attachment SHALL remain unbound until it is explicitly attached to an owner

#### Scenario: A pending reference is bound to an owner

- **WHEN** an unbound attachment reference is attached to an issue body or a comment
- **THEN** the attachment SHALL become owned by that issue body or comment

#### Scenario: Discarded compose state does not leave owned attachments

- **WHEN** a user discards compose state that held unbound pending attachment references
- **THEN** those references SHALL NOT become bound to any owner
- **AND** they SHALL NOT appear as attachments on any issue or comment

### Requirement: REQ-ATT-003 Three input paths feed one upload flow

The issue body editor and each comment composer SHALL accept files through three input paths: a browse/pick button, clipboard paste, and drag-and-drop onto the editor area. All three input paths SHALL route through the same upload-then-attach flow and SHALL produce the same kind of attachment reference.

#### Scenario: Browse button uploads a selected file

- **WHEN** a user activates the browse button and selects one or more files
- **THEN** each selected file SHALL be uploaded through the upload-then-attach flow

#### Scenario: Clipboard paste uploads a pasted file

- **WHEN** a user pastes from the clipboard (for example a screenshot) within the issue body editor or a comment composer
- **THEN** the pasted file SHALL be uploaded through the upload-then-attach flow

#### Scenario: Drag-and-drop onto the editor area uploads

- **WHEN** a user drags one or more files onto the editor area and drops them
- **THEN** each dropped file SHALL be uploaded through the upload-then-attach flow

#### Scenario: All input paths produce the same attachment reference kind

- **WHEN** a file is added via the browse button, via paste, or via drag-and-drop
- **THEN** each path SHALL produce the same kind of stable attachment reference

### Requirement: REQ-ATT-004 Content-aware markdown embedding on attach and remove

When an image attachment is attached, the editor SHALL insert a markdown image reference of the form `![name](att:id)` at the cursor. When a non-image attachment is attached, the editor SHALL insert a card reference of the form `[name](att:id)` at the cursor. Removing an attachment SHALL strip every `att:id` reference to that attachment from the markdown body of its owner.

#### Scenario: Image attachment inserts an image reference

- **WHEN** an image-typed attachment is attached
- **THEN** a markdown image reference `![name](att:id)` SHALL be inserted at the cursor

#### Scenario: Non-image attachment inserts a card reference

- **WHEN** a non-image attachment is attached
- **THEN** a markdown card reference `[name](att:id)` SHALL be inserted at the cursor

#### Scenario: Removing an attachment strips its markdown reference

- **WHEN** an attachment is removed
- **THEN** every `att:id` reference to that attachment SHALL be removed from the owner's markdown body

### Requirement: REQ-ATT-005 Attachment chip and live upload progress

Each in-flight or completed attachment SHALL render as a chip that shows an image thumbnail for image types, or a colored extension badge for other types, together with the filename, the size, and a remove control. While an attachment is uploading, its chip SHALL show a live progress indicator. The issue body editor and each comment composer SHALL carry independent attachment state so attachments are authored in context.

#### Scenario: Image attachment chip shows a thumbnail

- **WHEN** a completed attachment is an image
- **THEN** its chip SHALL display an image thumbnail, the filename, the size, and a remove control

#### Scenario: Non-image attachment chip shows an extension badge

- **WHEN** a completed attachment is not an image
- **THEN** its chip SHALL display a colored extension badge, the filename, the size, and a remove control

#### Scenario: In-flight attachment shows live progress

- **WHEN** an attachment is still uploading
- **THEN** its chip SHALL show a live progress indicator

#### Scenario: Per-surface attachment state is independent

- **WHEN** the issue body editor and one or more comment composers each hold attachments
- **THEN** each surface SHALL maintain its own independent attachment state

### Requirement: REQ-ATT-006 Local-filesystem storage reusing the artifact-storage shape

Attachment content SHALL be stored on the local filesystem under the `~/.mohist/attachments/` root, using server-generated storage paths, path-traversal guards, sanitized paths, atomic writes, and a metadata sidecar, mirroring the shape of `IWorkflowArtifactStorage`. Storage SHALL be exposed behind a single interface so that an object-store backend can be added later without changing callers.

#### Scenario: Content is stored under the attachments root

- **WHEN** attachment content is persisted
- **THEN** it SHALL be stored under the `~/.mohist/attachments/` root

#### Scenario: Storage paths are server-generated and traversal-guarded

- **WHEN** attachment content is stored
- **THEN** the storage path SHALL be server-generated
- **AND** any path-traversal attempt SHALL be rejected

#### Scenario: Writes are atomic with a metadata sidecar

- **WHEN** attachment content is written
- **THEN** the write SHALL be atomic
- **AND** a metadata sidecar SHALL record the attachment's recorded fields

#### Scenario: Storage is behind a single swappable interface

- **WHEN** a future object-store backend is introduced
- **THEN** callers SHALL be able to adopt it through the single storage interface without changing call sites

### Requirement: REQ-ATT-007 Persistence and project scoping

Attachment content and attachment metadata SHALL persist across server restarts. Attachments SHALL be scoped to the project that owns the issue or comment; an attachment SHALL NOT be accessible outside its owning project.

#### Scenario: Attachments survive a server restart

- **WHEN** the server restarts after attachments have been created
- **THEN** the attachment content and metadata SHALL remain available

#### Scenario: Attachments are scoped to the owning project

- **WHEN** an attachment is owned by an issue or comment in a project
- **THEN** the attachment SHALL NOT be accessible from any other project

### Requirement: REQ-ATT-008 Safe serving of attachment content

Serving attachment content SHALL use server-generated storage paths only and SHALL reject any path-traversal attempt. The original filename SHALL be restored only via the `Content-Disposition` header. Non-image content SHALL be served with a non-executable disposition so browsers do not execute it inline. Downloading an attachment SHALL preserve the original filename.

#### Scenario: Path traversal is rejected when serving

- **WHEN** a request attempts to reach content outside the server-generated storage path
- **THEN** the request SHALL be rejected

#### Scenario: Original filename is restored only via Content-Disposition

- **WHEN** attachment content is served
- **THEN** the original filename SHALL be conveyed only via the `Content-Disposition` header

#### Scenario: Non-image content is served non-executable

- **WHEN** non-image attachment content is served to a browser
- **THEN** it SHALL be served with a non-executable disposition

#### Scenario: Download preserves the original filename

- **WHEN** a user downloads an attachment
- **THEN** the downloaded file SHALL carry the attachment's original filename

### Requirement: REQ-ATT-009 Configurable upload size and count limits

Upload size and per-owner attachment count SHALL be bounded by configurable limits. An upload that exceeds the configured size limit SHALL be rejected. An attach operation that would exceed the configured per-owner count limit SHALL be rejected. Each rejection SHALL return a clear message identifying which limit was exceeded.

#### Scenario: Oversized upload is rejected

- **WHEN** a user uploads a file larger than the configured size limit
- **THEN** the upload SHALL be rejected
- **AND** a clear message SHALL indicate the size limit was exceeded

#### Scenario: Per-owner count limit is enforced

- **WHEN** attaching a file would exceed the configured per-owner attachment count limit
- **THEN** the attach SHALL be rejected
- **AND** a clear message SHALL indicate the count limit was exceeded

### Requirement: REQ-ATT-010 Author-initiated removal while the owner is editable

An attachment SHALL be removable by its author while the owning issue body or comment is still editable. An attachment SHALL NOT be removable by a non-author. Removal SHALL clear the attachment's `att:id` reference from the owner's markdown body and SHALL remove the stored content (or mark it for removal).

#### Scenario: Author removes an attachment while the owner is editable

- **WHEN** the author of an attachment removes it while the owning issue body or comment is still editable
- **THEN** the attachment SHALL be removed
- **AND** its `att:id` reference SHALL be cleared from the owner's markdown body

#### Scenario: Non-author cannot remove an attachment

- **WHEN** a user who is not the attachment's author attempts to remove it
- **THEN** the removal SHALL be rejected
