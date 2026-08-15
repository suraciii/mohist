# Tasks: Direct API activation boundary

- [x] Define the no-placeholder activation rule for `/api/v1`.
- [x] Define durable source identity, revision, checkpoint, and projection
      transaction boundaries.
- [x] Define source-position freshness and stream-generation recovery rules.
- [ ] Implement the first vertical read slice: projection schema/projector,
      Bearer-PAT boundary, concrete public read handler, and focused tests.
- [ ] Implement idempotent launch, then follow-up, fenced stop, and event
      stream routes as separate concrete slices.
