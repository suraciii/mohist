# Slack Adapter in Go

Status: accepted

## Problem

The Slack adapter runs as one process per machine, installed as an OS
service. The Node implementation required a Node.js >= 22.19 runtime on every
host, which complicated installation and update. The repository also needed a
low-risk pilot for the Go toolchain before considering larger ports.

## Decision

The adapter is a static Go binary in `packages/go/mohist-slack`, built with
`slack-go/slack`, `gorilla/websocket`, the standard library HTTP client, and
`log/slog`. The behavioral contract defined in [`../slack.md`](../slack.md)
does not change: wire behavior at the Server HTTP boundary and at the Slack
Socket Mode and Web API boundary is preserved, service names are unchanged,
and install and update flows keep their shape. The Node implementation is
deleted.

## Alternatives considered

**Keep the Node adapter.** No port risk, but every host keeps the Node
runtime requirement, and the repository gains no Go toolchain evidence for
later decisions.

## Consequences

Accepted deltas from the Node implementation:

- The port keeps slack-go's managed ping timeout instead of the Node 24-hour
  proxy override. The HTTP proxy is configured on both the API client and the
  WebSocket dialer, so liveness checks already run through the tunnel.
- A buffered-channel semaphore replaces the 5 ms in-flight poll. The
  observable contract, at most N concurrent events per process, is unchanged.
- Standard `log/slog` text or JSON output replaces the hand-rolled logfmt
  line format. Line formats intentionally diverge.
- Unknown JSON fields are tolerated; required-field validation errors are
  preserved exactly.

The pilot established the Go build, race-test, and CLI install and update
paths for the repository. The operator-facing configuration and update
surface lives in [`../../docs/self-host.md`](../../docs/self-host.md).
