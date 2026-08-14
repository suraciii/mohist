# Issue 557: Generic Reasoning Effort Capability

## Problem

Saved Agent definitions can carry a canonical reasoning effort, but the
current generic runtime contract exposes only model and variant catalogs. Pi's
native thinking levels are therefore easy to write into a field that other
runtime adapters ignore or misinterpret.

## Proposal

Freeze `(runtime, model, reasoningEffort, variant)` in the durable execution
snapshot. Publish a versioned, complete runtime capability entry with separate
reasoning-effort and variant maps. Resolve the tuple before admission and let
the selected runtime adapter translate the canonical effort privately.

## Safety boundary

An unavailable or incomplete catalog leaves work pending. An explicit,
complete incompatibility is a deterministic preflight failure. No runtime may
silently drop an effort, alias it to a variant, or translate a Pi-native value
for another runtime. This change does not invent runner admission policy; it
defines the capability evidence that admission must consume.
