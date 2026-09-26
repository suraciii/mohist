# Outbound Webhooks Design

## Design Drivers

- Mohist is a general HTTP webhook producer, not an adapter for a named
  receiver. Outbound webhook is Mohist's Open Host Service.
- CloudEvents is the Published Language. Mohist-specific HMAC is not the v1
  contract; only existing subscriptions may retain it for compatibility.
- Delivery is one-way. An authenticated inbound flow is a separate capability;
  this spec defines no loop protection for it or target-side security policy.
- Web and CLI configure the same Project resource.
- Delivery failure must not block event publication or change domain state.

## Model

`WebhookSubscription` is a Project-scoped resource. It owns event selection,
TargetUrl, authentication configuration, and lifecycle. It owns no Session,
execution, or receiver state.

The resource has a stable Project-scoped identity and readable name. Its target
is an `http` or `https` URL. Event selection is either `all` or a non-empty set
of catalog event types, with an optional CEL filter. Authentication is `none`,
`bearer`, `basic`, or `custom`. Lifecycle is `active`, `disabled`, or
`archived`.

Credentials belong to a separate secret store. Plaintext exists only in
process memory while sending. It never enters the subscription, logs,
transcript, API, CLI output, or failure record.

Write-time invariants:

- A selected event set is non-empty and contains only catalog event types.
- An empty CEL filter means no additional filter. A non-empty filter must
  compile.
- `TargetUrl` must use `http` or `https`.
- `AuthType` must be `none`, `bearer`, `basic`, or `custom`.
- Credentials are encrypted at rest and redacted from every read surface.
