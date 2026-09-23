# Workflow Artifacts

This document owns how the Server ingests uploaded Workflow artifact content: the file and
directory upload transports, the directory envelope format, the limits that bound ingestion, the
retention bound, and the rollback rule on failure. The role of an artifact (evidence, provisioning
input, recovery) lives in [`plan-artifacts.md`](plan-artifacts.md).

## Design Drivers

- A directory artifact may contain hundreds of files and reach the configured total size. Buffering
  the whole envelope and decoding every entry into memory made peak retention several times the
  payload.
- The wire format is split between the Runner (producer) and the Server (consumer). Both sides must
  agree on one format; the repository keeps no backward-compatibility layer.
- Streaming must not weaken validation. The file-count, single-file, and total-size limits keep
  being enforced per entry as the content is consumed.
- An oversized or malformed body must fail before the Server retains it, and a failed directory
  write must leave no listable or provisionable content.

## Model

A directory artifact upload carries one envelope as the `content` multipart part with
`Content-Type: application/x-mohist-artifact-directory`. Single-file artifacts keep their plain
stream upload and are unchanged.

The envelope is newline-delimited JSON (NDJSON): a sequence of top-level JSON values. The first
value is a header; every following value is one contained file.

```text literal
{"kind":"directory"}
{"path":"a.md","size":5,"contentHash":"sha256:...","contentType":"text/markdown","data":"YWxwaGE="}
{"path":"sub/b.md","size":6,"contentType":"application/json","data":"YmV0YSE="}
```

- The header must declare `kind: "directory"`.
- A file value requires `path`; `data` is base64 content; `size` is the declared decoded size;
  `contentHash` and `contentType` are optional and preserved.
- The request `size` is the envelope byte length. The request `contentHash` is the envelope hash
  used for upload idempotency. Each file's declared `contentHash` is verified by storage.

The Server exposes each value as a `WorkflowArtifactDirectoryEntryInput`, and storage consumes the
sequence as an `IAsyncEnumerable`.

## Semantics

### Envelope parsing

The Server parses the sequence with
`JsonSerializer.DeserializeAsyncEnumerable<T>(stream, topLevelValues: true, ...)`, so one value is
materialized at a time. The reader validates the header, decodes each value's base64 into a
`byte[]`, and yields the entry. It rejects a null value, a missing or wrong header, a header-only
directory, and a missing path.

### Ingestion limits

`WorkflowArtifactDirectoryLimits` carries the durable limits:

| Limit | Default | Meaning |
| --- | --- | --- |
| `MaxFileCount` | 2,000 | maximum contained files |
| `MaxFileBytes` | 64 MiB | maximum single decoded file |
| `MaxTotalBytes` | 256 MiB | maximum decoded total |
| `MaxEnvelopeBytes` | 384 MiB | maximum encoded envelope |
| `MultipartFramingSlackBytes` | 64 KiB | fixed multipart framing allowance |
| `MaxMultipartBodyBytes` | `MaxEnvelopeBytes + slack` | HTTP request body bound |

The envelope default covers the decoded total after base64 inflation (4/3) plus JSON framing.
Configuration that raises `MaxTotalBytes` raises `MaxEnvelopeBytes` with it; when the two disagree,
the smaller value is the effective gate.

Enforcement is fail-closed and two-layered:

1. The upload service rejects a directory request whose declared `Size` exceeds `MaxEnvelopeBytes`
   before the content stream is opened.
2. The reader caps reads at `min(MaxEnvelopeBytes, declaredSize)` through a bounded stream that
   never pulls more from the underlying stream, then probes for exactly one trailing byte after the
   enumeration. A present byte means the actual body exceeded the declared size or the envelope
   limit, and the request fails closed. End of stream confirms the body was fully consumed; the
   reader then requires the read count to equal the declared size, which rejects a body shorter
   than declared.

### Transport limits

Kestrel's default request-body cap and `FormOptions.MultipartBodyLengthLimit` are below a legal
default envelope. Directory ingestion therefore uses dedicated routes whose request-body and
multipart/form limits derive from `MaxEnvelopeBytes` plus the framing slack:

- `POST /api/workflow-runs/{workflowRunId}/work/{workId}/artifact-directory-uploads`
- `POST /api/agent-jobs/{agentJobId}/work/{workId}/artifact-directory-uploads`

Those routes carry a `RequestSizeLimitAttribute` with `MaxMultipartBodyBytes` and replace the
request's form feature with a `MultipartBodyLengthLimit` of the same bound before reading the form.

Only the directory routes raise the limits. The original `artifact-uploads` routes keep the
default Kestrel/form boundary, so the directory relaxation cannot widen regular file uploads or
attachments; the file routes reject the directory content type and the directory routes reject a
non-directory content type. `FormOptions.MultipartBodyLengthLimit` stays at its framework default,
so `AttachmentStorageOptions.MaxFileBytes` enforcement is unchanged.

### Streaming storage

`IWorkflowArtifactStorage.WriteDirectoryAsync` accepts an
`IAsyncEnumerable<WorkflowArtifactDirectoryEntryInput>`. Each adapter pulls one entry, validates it
(normalized path, duplicate detection, declared size, running count and running total), copies the
content while hashing and enforcing the actual-byte limits, and records manifest metadata before
requesting the next entry. Entry N+1 is not parsed or decoded until entry N is written.

The committed manifest holds metadata only (path, size, hash, content type) and is sorted by ordinal
relative path before it is written. A header-only directory is rejected. Both adapters register
nothing unless the whole sequence succeeds.

### Retention

Peak retained payload is bounded by one entry plus fixed buffers: the current entry's base64 string
and decoded `byte[]` (about 2.33 times its decoded size), the storage copy buffer, and the hash
state. Earlier entries' content is released before the next value is parsed. The manifest holds
metadata only. `MaxEnvelopeBytes` bounds a lying declared size; `MaxFileBytes` bounds a truthful
single entry.

### Failure and cancellation

- Malformed envelope, wrong kind, empty directory, invalid base64, declared-size mismatch (larger
  or smaller than actual), envelope-limit breach, entry-limit breach, and content-hash mismatch all
  surface as `Invalid` (HTTP 400) with a diagnostic message.
- `WriteDirectoryAsync` removes the collection directory on any failure, so a partially written
  directory is never listable or provisionable.
- The pending upload row is created only after the storage write succeeds. On any failure the
  upload service deletes the partial storage with `CancellationToken.None`, independent of the
  request token.
- Cancellation propagates through the async enumerator and the content stream. `await foreach` and
  `await using` dispose them, and the same rollback path removes partial storage.

## Examples

A valid two-file directory records both entries with their path, decoded size, computed hash, and
declared content type:

```text literal
{"kind":"directory"}
{"path":"a.md","size":5,"contentType":"text/markdown","data":"YWxwaGE="}
{"path":"sub/b.md","size":6,"data":"YmV0YSE="}
```

A body whose actual envelope is larger than its declared `Size` is rejected by the trailing-byte
probe before the extra bytes are retained.

## Status

Implemented. The directory transport, `MaxEnvelopeBytes`, streaming reader and storage, and the
dedicated directory upload routes ship together; the Runner selects the directory route for
directory captures. Single-file artifact ingestion and its default transport boundary are
unchanged.
