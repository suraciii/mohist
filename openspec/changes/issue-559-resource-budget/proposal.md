# Bound the full verification resource budget

The full verification action currently applies a 4096 MiB Linux `prlimit`
address-space/data bound. The Server UnitTests process can exceed that virtual
address-space limit before its RSS approaches the host limit, so workflow
verification is reported as `resource-containment` even when the suite is
otherwise healthy.

This change raises only the named `full-verify` command profile to a finite
16384 MiB bound. It does not disable per-work containment, change ordinary work
limits, or retry an already failed run.
