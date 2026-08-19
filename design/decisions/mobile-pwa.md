# Mobile PWA and Push Notifications

> This document records open questions about an unimplemented product
> proposal.

## Background

An autonomous Workflow should not require constant attention. When a user
leaves their desk, failing to notice that a plan is ready limits throughput.
Mobile access and push notifications may be part of the self-hosted autonomous
system's product promise.

## Original Proposal

Use a PWA, with scenarios in this priority order:

1. Decide an Approval when a plan or check is ready.
2. Observe the board and progress.
3. Intervene with force stop or retry.
4. Start work from the backlog.
5. Create a quick backlog item.

Possible deliverables include PWA infrastructure (manifest and service worker),
Web Push, mobile versions of core pages (Approval, optimized board, and a
simplified Issue page), and desktop-only settings.

## Current State

- Only part of KanbanBoard has a mobile adaptation (`md:hidden`). Other pages
  are not adapted.
- There is no PWA infrastructure or Web Push.
- `mo notification setup` exists for outbound Hermes chat-platform
  notifications. Those are chat notifications, not in-browser Web Push.

## Open Questions

1. **Is mobile a core need?** Do individual developers need to decide
   Approvals from a phone, or are desktop access and Hermes chat notifications
   sufficient?
2. **PWA or Hermes notifications?** Are they redundant, or is Hermes the
   message stream while the PWA provides direct actions?
3. **HTTPS for self-hosting:** Web Push requires HTTPS. Certificate handling is
   undecided: mkcert, Caddy, or a built-in option.
4. **Minimum viable version:** Should it provide only plan-ready push and a
   mobile Approval page, or adapt every page?
5. **VAPID keys and multi-device subscription management.**
