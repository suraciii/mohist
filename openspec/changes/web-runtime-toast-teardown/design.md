# Runtime Toast Timer Teardown

## Problem

`RuntimeToastHost` owns one auto-dismiss timer per toast, but the timer map is
not cleared when the host unmounts. A timer can therefore run after the test
environment or browser document that created the host has been torn down. In
the Web test suite this surfaced as an unhandled `window is not defined`
failure from the timer callback.

## Invariant

After `RuntimeToastHost` unmounts, it owns no live auto-dismiss timers. A timer
callback must never update state belonging to an unmounted host.

## Design

Keep timer ownership local to `RuntimeToastHost`. Register one effect cleanup
that clears every timer in the existing map and empties the map. Existing
positive-TTL dismissal, manual dismissal, and `clear()` behavior remain
unchanged.

## Regression

With fake timers, push a positive-TTL toast, unmount the host, and assert that
the timer count returns to its pre-render baseline. This tests the lifecycle
boundary directly without waiting on wall-clock time or depending on suite
ordering.

## Scope

Only the toast host lifecycle and its focused test are in scope. No changes to
delivery-failure classification, notification rendering, or #570 workflow
behavior are required.
