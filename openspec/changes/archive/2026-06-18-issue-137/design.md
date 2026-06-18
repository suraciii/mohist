## Context

Today issue bodies and comments are plain markdown only. The only binary-upload path is the internal runner→server route `POST /api/workflow-runs/{id}/work/{workId}/artifact-uploads` (`WorkflowArtifactUploadRoutes` → `WorkflowArtifactUploadService`), scoped to an active workflow work item — not usable by a user. Comments carry only a markdown body (`IssueCommentRow { Id, ProjectId, IssueId, IssueNumber, Body }`, created by `IssueGrain.AddCommentAsync(string body)`). The Web UI has no upload affordance and `MarkdownReader` has no notion of attachment references.

Three proven primitives are reused as anchors:
- **Storage shape**: `IWorkflowArtifactStorage` / `FileSystemWorkflowArtifactStorage` — server-generated paths (`GenerateStoragePath`), `SanitizeRelativePath` traversal guards, atomic `.tmp`→move writes (`WriteStreamAsync`), and a `metadata.json` sidecar, rooted under `~/.mohist/`.
- **Upload-then-bind pattern**: `WorkflowArtifactUploadService` writes content + a pending row (`WorkflowArtifactPendingUploadRow` with `ExpiresAt` TTL) first, then a later mutation binds it; `SafeRemoveStorageDirectory` rolls back partial writes.
- **Serving pattern**: `IssueRoutes.Artifacts.cs` resolves the storage path server-side from an id lookup and streams via `Results.Stream` with a content-type fallback.

Stakeholders: product owners authoring evidence-rich issues; single-user local-node deployment (no per-user auth model today). Constraints: local filesystem first; must not preclude an object-store backend; must not disturb the existing artifact/session surfaces that share `MarkdownReader`.

See `proposal.md` for motivation and `specs/issue-attachments/spec.md` + `specs/markdown-reader/spec.md` for the normative requirements this design implements.

## Goals / Non-Goals

**Goals:**
- Let a user attach files (including images) to an issue body and to comments via pick, paste, and drag-and-drop, through one upload-then-attach flow.
- Store attachment content on the local filesystem under `~/.mohist/attachments/`, reusing the security-hardened artifact-storage shape, behind a single swappable interface.
- Render `att:id` references on issue/comment surfaces as inline images (click-to-preview lightbox) or downloadable file cards, via `MarkdownReader`.
- Serve content safely (server-generated paths, `Content-Disposition` filename, non-executable for non-images) and bound uploads by configurable size/count limits.
- Persist attachments across restarts, scoped to the project, with author-initiated removal that also strips the markdown reference.

**Non-Goals:**
- Object-store/S3 backend in this pass (interface-only, kept open).
- Attachments on chat or any surface beyond issue bodies and comments.
- Replacing the markdown editor (attachments layer onto the existing markdown body / `<Textarea>`).
- Versioning or an attachment library/management view.
- Per-user authorization model (single-user local deployment; removal is gated on owner editability, not an ACL).
- Workflow/task artifacts (agent-produced outputs) — unchanged.

## Decisions

### Decision 1: Separate `IAttachmentStorage` interface, own root `~/.mohist/attachments/`

Introduce a new `IAttachmentStorage` (+ `FileSystemAttachmentStorage`) modeled directly on `IWorkflowArtifactStorage`, rooted at `~/.mohist/attachments/`, with on-disk layout `attachments/{projectId}/{attachmentId}/content` + `metadata.json`. The `projectId` segment enforces project scoping at the storage layer.

- **Rationale**: reuses the proven security primitives (server-generated paths, `SanitizeRelativePath`, atomic `.tmp` move, metadata sidecar) while keeping user-authored content isolated from agent-produced artifacts and letting the surface evolve independently. The `projectId` segment makes cross-project leakage structurally impossible.
- **Alternative A — reuse `IWorkflowArtifactStorage` directly**: rejected. Its path layout (`workflows/{runId}/tasks/{taskRunId}/artifacts/{artifactId}`) is workflow-specific; mixing user content into the artifact root blurs the distinct domains and couples cleanup lifecycles.
- **Alternative B — one shared root, different segment**: rejected for the same coupling reason; a dedicated root makes the future object-store swap a single-interface change.

### Decision 2: Single `AttachmentRow` with a nullable owner (upload-then-bind)

`AttachmentRow { Id ("att_<guid>"), ProjectId, OwnerKind ("issue"|"comment"|null), OwnerId, OriginalFileName, ContentType, Size, StoragePath, CreatedAt, ExpiresAt }` as an EF Core row in the existing SQLite store, mirroring `IssueCommentRow`. Upload creates the row with `OwnerKind = null` and an `ExpiresAt` TTL (the pending state); the owning mutation (comment create / issue body update) binds `OwnerKind`+`OwnerId` and clears `ExpiresAt`.

- **Rationale**: the spec's data-model principle is "as simple as possible." A single nullable-owner row implements upload-then-attach (REQ-ATT-002) without a second table, while the TTL mirrors the proven `WorkflowArtifactPendingUploadRow.ExpiresAt` cleanup pattern for discarded compose state.
- **Alternative — two tables (pending + bound)** like workflow artifacts: rejected as over-engineering for this scope; nullable-owner captures the same lifecycle with one entity and one bind transition.
- Bind happens at the owning mutation, not as a free-standing step: `AddCommentAsync` and the issue-body update carry the attachment ids to bind, so ownership is always consistent with the persisted markdown body (which already contains the `att:id` references).

### Decision 3: `att:id` logical reference scheme, resolved only at render time

Attached content is referenced in markdown as `![name](att:<attachmentId>)` (image) or `[name](att:<attachmentId>)` (non-image). The `att:` pseudo-protocol is never a real URL; it is resolved to a serving URL by `MarkdownReader` on issue/comment surfaces, and the storage path stays server-side only.

- **Rationale**: the reference is logical, so it survives storage-root/config changes and never leaks server paths into authored content. Removing an attachment strips its `att:id` token from the body (REQ-ATT-004).
- **Alternative — embed real serving URLs in markdown**: rejected because URLs break when the serving path changes and would leak internal paths.

### Decision 4: Serving route mirrors artifact serving, adds `Content-Disposition`

Add `GET .../issues/{number}/attachments/{attachmentId}/content` (and a comment-scoped variant) that looks up the row, verifies `ProjectId` matches the route, then streams via `storage.OpenFileContent(...)` + `Results.Stream`, mirroring `IssueRoutes.Artifacts.cs`. Add what artifact serving lacks: `Content-Disposition: inline; filename="<original>"` for a safe image allowlist, and `attachment; filename="<original>"` for everything else.

- **Rationale**: path traversal is structurally impossible — the storage path comes from the id lookup, not the request. Content type comes from the row (fallback `application/octet-stream`). Non-images get `attachment` so browsers never execute them (REQ-ATT-008).
- **SVG / XSS**: treat image types outside a safe allowlist (png/jpeg/gif/webp) — including SVG — as non-image (`attachment`), so uploaded SVG never renders/executes inline.
- **Alternative — a single global `/api/attachments/{id}/content`**: rejected; routing under the project+issue path keeps the project-scope check co-located with the resource and consistent with neighboring issue routes.

### Decision 5: Upload-then-attach endpoints

- `POST /api/projects/{projectId}/attachments` (multipart, single file) → validates size against the configured limit, writes content atomically, creates a pending `AttachmentRow` (`OwnerKind = null`), returns `{ id, fileName, contentType, size }`. This is the one call all three input paths hit.
- Bind is performed by the owning mutation: extend `AddCommentAsync` and the issue-body update to accept the attachment ids being claimed; the server binds them and clears `ExpiresAt`. The markdown body sent in the same call already carries the `att:id` references.
- `DELETE .../issues/{number}/attachments/{attachmentId}` (and comment-scoped) → author-initiated removal: deletes content, strips the `att:id` reference from the owner body, removes the row. Gated on the owner still being editable.

- **Rationale**: one upload endpoint keeps the three input paths trivially uniform (REQ-ATT-003); binding at the owning mutation keeps ownership consistent with the persisted body and avoids orphan bindings when the user discards compose state.
- **Alternative — a free-standing `/attach` call per attachment**: rejected; it would let pending refs be bound independently of the body save, risking attachments bound to an owner whose body never references them.

### Decision 6: Reusable `AttachmentComposer` + an opt-in `MarkdownReader` resolver

- **`AttachmentComposer`** (new shared widget) wraps the existing `<Textarea>` and adds the three input paths: a browse button, `onPaste` (clipboard screenshot), and `onDrop`/`onDragOver` (full-card dashed overlay). It owns per-surface attachment state, renders chips (image thumbnail or extension badge, filename, size, ×) with a live progress bar, and inserts/strips `att:id` references in the textarea value as attachments are added/removed. It is used by both `EditIssueDialog` (issue body) and the comment composer in `IssueDetailPage`, giving each surface independent state (REQ-ATT-005).
- **`MarkdownReader`** gains an optional `resolveAttachment?(id)` prop. On issue/comment surfaces the page passes a resolver that returns `{ url, contentType, fileName, size } | null`. The reader's `image`/`link` overrides detect `att:` hrefs: images render inline and open a lightbox (portal overlay, dark backdrop, click-to-dismiss) on click; non-images render a file card; unresolved refs render a safe fallback (REQ-MDR-010 scenarios).

- **Rationale**: keeping the resolver opt-in preserves `MarkdownReader`'s existing generality — artifact, session, and review surfaces that don't pass a resolver are unchanged.
- **Alternative — bake attachment knowledge into `MarkdownReader`**: rejected; it would couple the shared reader to the attachment feature and risk the artifact/session surfaces.

### Decision 7: Configurable limits via `AttachmentStorageOptions`

Mirror `WorkflowArtifactStorageOptions`: config section `Mohist:AttachmentStorage`, env `MOHIST_ATTACHMENT_ROOT`, plus `MaxFileBytes` (default ~25 MB) and `MaxCountPerOwner` (default ~20). Size is checked before/while reading the multipart body (reject early, never buffer an oversized file fully); count is checked at bind.

- **Rationale**: matches the existing options/config conventions and gives operators a single knob. Early size rejection avoids a memory/DoS pitfall.
- **Alternative — hard-coded limits**: rejected; deployments differ (a screenshots-heavy project vs a logs-heavy one).

## Risks / Trade-offs

- [Local FS only — no HA/multi-node sharing] -> Mitigation: interface-based storage; S3 backend is a later single-interface swap. Acceptable for the current single-node deployment.
- [Unbound pending uploads accumulate when compose is discarded] -> Mitigation: `ExpiresAt` TTL + a hosted cleanup job reusing the `WorkflowArtifactPendingUploadRow` cleanup shape; bind at the owning mutation so successful saves never leave orphans.
- [`att:id` references dangle after out-of-band removal or DB/content drift] -> Mitigation: the removal endpoint strips references from the body; `MarkdownReader` renders a safe fallback for any unresolved `att:` ref; content-missing serves a 404 like artifact serving.
- [XSS via uploaded SVG/HTML disguised as an image] -> Mitigation: only a safe image allowlist (png/jpeg/gif/webp) is served `inline`; everything else (including SVG) is served `attachment` with a non-executable disposition.
- [Oversized upload consumes memory] -> Mitigation: reject on `Content-Length`/streamed size cap before fully buffering; enforce `MaxFileBytes` at the multipart read.
- [`MarkdownReader` is shared — attachment resolution must not affect artifact/session surfaces] -> Mitigation: resolver is opt-in via prop; default behavior (no resolver) is bit-for-bit unchanged; covered by extending the existing reader component tests.
- [No per-user auth — "author" removal is best-effort] -> Mitigation: gate removal on owner editability rather than an ACL; record `AuthorId` when identity is available so a future auth model can enforce it. Tracked as an open question.

## Migration Plan

This change is **additive only** — no existing table, endpoint, or component behavior changes for current users.

1. **Backend first**: add the EF Core `AttachmentRow` (+ migration creating the table), `IAttachmentStorage`/`FileSystemAttachmentStorage` (+ options), the upload/bind/serve/remove endpoints, and `AddCommentAsync`/issue-update binding. The `~/.mohist/attachments/` root is auto-created on first use (like `Directory.CreateDirectory(_root)` in the artifact storage ctor).
2. **Web second**: ship `AttachmentComposer` into `EditIssueDialog` and the comment composer, and the opt-in `MarkdownReader` resolver + lightbox.
3. **Rollback**: revert the web change, then the server change. The new table and storage root can be dropped; no pre-existing issue/comment body references `att:`, so there is no dangling-reference cleanup and no backfill is required.
4. **No data migration**: existing issues/comments are untouched (attachments reference owners, not vice versa).

## Open Questions

- **Author identity**: in single-user local mode there is no user id. Should removal be gated purely on owner editability (lean), or should we introduce a sentinel author id now? Affects REQ-ATT-010 enforcement strictness.
- **Image allowlist**: is the png/jpeg/gif/webp set sufficient, or should AVIF/HEIC be included? Affects inline-rendering scope.
- **Soft vs hard delete on removal**: spec says removal deletes stored content. Confirm hard-delete (lean) vs. a soft-delete/tombstone for undo.
- **Default limits**: confirm ~25 MB / ~20-per-owner defaults, and whether limits should be per-project configurable or global.
- **EXIF/metadata stripping** for uploaded images: out of scope for v1 — confirm we accept images as-is.
