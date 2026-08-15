# Tasks: Direct API activation boundary

- [x] Define the no-placeholder activation rule for `/api/v1`.
- [x] Define durable source identity, revision, checkpoint, and projection
      transaction boundaries.
- [x] Define source-position freshness and stream-generation recovery rules.
- [x] Implement the first Job-only vertical read slice: Job-revision snapshot,
      Bearer-PAT boundary, Project-grant check, concrete public read handler,
      and focused tests. Session/Input/Turn/event projection remains separate.
- [ ] Implement idempotent launch, then follow-up, fenced stop, and event
      stream routes as separate concrete slices.
