# Composite Issues

Status: accepted

## Problem

When one requirement spans multiple Repositories, its parts must move through
separate Workflows while one Issue tracks the whole. Multi-Repository resources
made this a real need; under one Repository per Project, child Issues had
appeared to overlap with Epic.

## Decision

An Issue becomes composite through an explicit `parent` reference chosen by the
owner. Decomposition is never automatic. A child Issue divides one unit of work:
a partial result has no product value, and startable children advance
concurrently. The Epic axis is unchanged: Epic organizes a product goal and
controls work in progress serially. The model, invariants, and lifecycle rules
live in [`../composite-issues.md`](../composite-issues.md).

## Alternatives considered

**Automatic three-stage breakdown.** An early proposal suggested Agent
analysis, an `issue-breakdown.json` artifact, Approval, and bulk child-Issue
creation. Rejected: decomposition is always an explicit choice by the owner or
an External Agent acting for the owner; automatic breakdown and bulk generation
remain Non-goals.

**Epic-only organization.** Rejected when multi-Repository resources arrived:
Epic members deliver independent value and are supplied serially, while child
Issues divide one unit of work. The decomposition axis is independent of the
Epic axis.

## Consequences

- Epic behavior does not change; Epic treats a parent as an ordinary Issue.
- Parent state derives from child state and is never maintained manually.
- The full model, ownership rules, and lifecycle constraints are specified in
  [`../composite-issues.md`](../composite-issues.md), not duplicated here.
