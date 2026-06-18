## Why

Issue bodies and comments are plain markdown only — there is no way to attach a screenshot, log, config file, or mockup that is the actual evidence or subject of the work. Users must describe visual things in words, host images on a third party (which rots and loses context), or omit the evidence entirely. This undermines evidence-driven issue quality: a PO cannot attach the WebUI screenshot, the failing log, or the target mockup that defines the request. Attachments make the evidence travel with the issue.

## What Changes

- Add user-facing attachment support to issue bodies and comments: a user can add one or more files by picking, pasting (e.g. a clipboard screenshot), or dragging onto the editor area.
- Introduce an **upload-then-attach** flow: uploading produces a stable attachment reference; attaching links it to an owner (an issue or a comment). This decouples the compose UX (paste/drag mid-edit) from ownership and matches the existing artifact-upload pattern.
- Add **content-aware embedding**: attaching an image inserts a markdown image reference `![name](att:id)` at the cursor; non-image files become `[name](att:id)` card references. Removing an attachment strips its reference from the markdown.
- Render attached images **inline** in the rendered issue body / comment and make them click-to-preview full-size; render non-image files as **downloadable cards** (filename, size, type) that preserve the original filename on download.
- Persist attachments to the **local filesystem under `~/.mohist/attachments/`**, reusing the security-hardened shape of `IWorkflowArtifactStorage` (server-generated paths, traversal guards, atomic writes, filename restored only via Content-Disposition). The design must not preclude an object-store backend later.
- Bound uploads with **configurable size and count limits**; reject oversized uploads with a clear message.
- Allow an attachment's **author to remove it** while the issue/comment is still editable; removal also clears its markdown reference.
- Scope attachments to the project; attachments **survive a server restart**.

## Capabilities

### New Capabilities
- `issue-attachments`: User-uploaded files attached to issues and comments. Covers the Attachment domain model (stable id, original filename, content type, size, stored content location) and its attachable owner (an issue or a comment); the upload-then-attach flow; local-filesystem storage under `~/.mohist/attachments/` with the security-hardened shape of `IWorkflowArtifactStorage` (server-generated paths, traversal guards, atomic writes, Content-Disposition filename restoration); safe serving (no path traversal, no executable disposition for non-images); configurable upload size/count limits; the attachment chip + live progress UX in the issue body editor and comment composer; author-initiated removal with markdown reference cleanup; project scoping and persistence across restarts. Becomes `specs/issue-attachments/spec.md`.

### Modified Capabilities
- `markdown-reader`: Issue body and comment rendering (REQ-MDR-010) SHALL resolve attachment references (`att:id`): image-typed attachments render inline at their markdown position and open into a full-screen lightbox on click; non-image attachments render as downloadable file cards showing filename, size, and type. Today the reader renders issue/comment markdown with no notion of attachment references; this adds attachment-aware rendering to those two surfaces.

## Impact

- **Backend storage** (`packages/server/src/Mohist.Server/Workflow/Services/Storage/`): introduce a user-attachment storage abstraction modeled on `IWorkflowArtifactStorage` / `FileSystemWorkflowArtifactStorage`, rooted under `~/.mohist/attachments/`, reusing path-traversal guards, sanitized paths, atomic writes, and the metadata sidecar pattern. Kept behind one interface so an object-store backend can be added later without changing callers.
- **Backend data model & grains** (`IssueGrain`, `IssueCommentRow`): add an Attachment entity (stable id, original filename, content type, size, stored content location) with a nullable attachable owner linking it to one issue or one comment, mirroring the `WorkflowArtifactPendingUploadRow` → bind pattern. Persist attachment metadata so attachments survive restarts and are scoped to the project.
- **Backend HTTP API**: add user-facing endpoints for upload-then-attach (upload producing a stable pending reference; bind to an issue/comment owner), author removal while the owner is editable, and safe serving (server-generated paths only, original filename restored only via `Content-Disposition`, non-executable disposition for non-image types). This is distinct from the internal runner→server `POST /api/workflow-runs/{id}/work/{workId}/artifact-uploads` path (`WorkflowArtifactUploadRoutes`), which stays scoped to workflow work items.
- **Web UI** (issue body editor, comment composer, markdown rendering): add three input paths (drag-and-drop onto the editor card, clipboard paste, browse button) through one upload-then-attach flow; render attachment chips (image thumbnail or extension badge, filename, size, remove ×) with a live progress bar; insert `![name](att:id)` / `[name](att:id)` references and strip them on removal; add a Write/Preview toggle where Preview renders images inline and other files as cards; add a full-screen lightbox for inline images. Per-surface state keeps the issue body and each comment composer independent.
- **Markdown rendering** (`MarkdownReader`, `markdown-reader` spec): teach the reader to resolve `att:id` references on issue/comment surfaces into inline images (click-to-preview full-size) or downloadable file cards.
- **Configuration**: add configurable upload size and count limits with clear rejection messaging for oversized uploads.
- **Dependencies/systems**: local filesystem only for this pass (matches single-node deployment); no object-store/S3 backend in scope (kept open). No changes to workflow/task artifacts (agent-produced outputs), which remain on their existing internal path.
