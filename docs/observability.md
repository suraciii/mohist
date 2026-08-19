# Observability

Mohist should signal problems before they affect work and retain enough
evidence to explain their cause. Observation itself must not slow or block
work.

## What Users Can Learn

A user should be able to answer four questions quickly:

1. Is Mohist working correctly now?
2. Which operations are slow, failing, or consuming unusual resources?
3. When did the problem start, and what is its impact?
4. What should the user inspect or do next?

A live process is not necessarily a healthy system. Health must also reflect
slow responses, resource pressure, and observation data that has stopped or
been dropped.

## Three Signal Types

- **Metrics detect problems:** They show trends in speed, request volume,
  resource use, and data growth.
- **Traces explain problems:** They show the steps in one operation and where
  time and effort were spent.
- **Logs record events:** They record a specific failure, rejection, drop, or
  degradation and its cause.

Each signal type has one job. A user should not need to inspect many traces
manually to discover a problem.

## Safety Boundary

- Issues, Workflows, and AgentSessions continue to work when observation is
  disabled or fails.
- Observation data uses separate, bounded storage and does not compete with
  product data for disk space.
- When data grows too quickly, Mohist reduces or drops observation data before
  sacrificing core work.
- The status page clearly reports whether observation is off, healthy, or
  degraded, including storage use and dropped data.
- Default configuration supports long-running use without regular manual
  cleanup.
- Built-in observation is enabled by default with a separate 1 GiB storage
  budget and 72-hour retention. The OTLP receiver listens only on
  `localhost:4318` by default and is not exposed externally.
- Set `Mohist:Otel:Enabled=false` and restart Server to disable collection,
  receiving, diagnostic sampling, and background maintenance when resource or
  binding problems occur.

## Inspecting Status

Run `mo otel status` to see whether observation is `healthy`, `degraded`, or
`off`, and to inspect current resource protection.
