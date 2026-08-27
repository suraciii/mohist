# Keep the historical WorkflowRunProfile persistence name

Status: accepted

## Problem

The persisted `WorkflowRunProfile` name stores Run Variables even though it no
longer represents a Profile.

## Decision

The implementation keeps the historical name until that storage is
restructured for a product reason. This name is not a second domain meaning of
Workflow Profile.

## Alternatives considered

**Rename the persisted name now.** That rewrites production storage without
changing behavior, and the cost lands on every existing installation.

## Consequences

The name stays until storage is restructured for a product reason. Readers must
not treat the persisted name as domain language.
