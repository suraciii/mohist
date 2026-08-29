package mohistslack

import (
	"context"
	"errors"
	"log/slog"
	"regexp"
	"sync"
	"time"
)

const adapterDisconnectTimeout = 15 * time.Second

// Adapter ports packages/mohist-slack/src/adapter.ts: one independent
// runtime per target key, generation fencing across await boundaries,
// eviction that never deletes a successor, drain single-flight with
// uncertain recovery first, backpressure notices before acknowledgement,
// and message acknowledgements only after successful server ingress.
//
// Fencing maps the Node identity checks onto Go snapshots: a snapshot holds
// the lease id plus socket and web identities captured under the runtime
// lock; ensureCurrent panics StaleRuntimeError when any of them moved or the
// runtime left the map. Only that panic crosses library code (the delivery
// functions take ensureCurrent callbacks); every flow recovers it at its
// boundary and swallows it — a superseded runtime's failures are never acted
// on. Transport failures classify through drainOutcome instead of unwinding.

// Transport is the Server contract the adapter drives.
type Transport interface {
	Discover(ctx context.Context) ([]Target, error)
	AcquireLease(ctx context.Context, target Target, kind LeaseKind, adapterID string) (Lease, error)
	RenewLease(ctx context.Context, target Target, leaseID, adapterID string) (*LeaseRenewal, error)
	ReportHello(ctx context.Context, target Target, leaseID, appID string) (HelloOutcome, error)
	Ingress(ctx context.Context, target Target, envelope Envelope, leaseID, adapterID string) (IngressResult, error)
	Interaction(ctx context.Context, target Target, envelope InteractionEnvelope, leaseID, adapterID string) (InteractionResult, error)
	ClaimDelivery(ctx context.Context, target Target, leaseID, adapterID string) (*Delivery, error)
	ClaimUncertainDelivery(ctx context.Context, target Target, leaseID, adapterID string) (*Delivery, error)
	AckDelivery(ctx context.Context, target Target, ack DeliveryAck, leaseID string) error
}

// IsLeaseStale reports whether an error is the Server's
// lease_stale_or_expired rejection, which makes the caller drop the runtime
// without retrying inline.
func IsLeaseStale(err error) bool {
	var apiErr *APIError
	if errors.As(err, &apiErr) {
		return apiErr.Code == string(HelloLeaseStale)
	}
	return false
}

// Ticker abstracts time for tests; production uses real tickers and tests
// fire channels manually.
type Ticker interface {
	C() <-chan time.Time
	Stop()
}

type realTicker struct{ inner *time.Ticker }

func (t realTicker) C() <-chan time.Time { return t.inner.C }
func (t realTicker) Stop()               { t.inner.Stop() }

// AdapterOptions configures one Adapter.
type AdapterOptions struct {
	AdapterID      string
	Transport      Transport
	SocketFactory  SocketFactory
	WebFactory     WebFactory
	Logger         *slog.Logger
	DiscoveryEvery time.Duration
	HeartbeatEvery time.Duration
	DeliveryPoll   time.Duration
	MaxInFlight    int
	Dispose        func()
	TickerFactory  func(time.Duration) Ticker
}

// Adapter runs one runtime per discovered target.
type Adapter struct {
	opts      AdapterOptions
	log       *slog.Logger
	newTicker func(time.Duration) Ticker

	baseCtx    context.Context
	cancelBase context.CancelFunc

	mu       sync.Mutex
	runtimes map[string]*runtime
	started  bool
	stopped  bool

	sem      chan struct{}
	stopCh   chan struct{}
	stopDone chan struct{}
	wg       sync.WaitGroup

	disposeOnce sync.Once
}

type runtime struct {
	key    string
	target Target

	mu     sync.Mutex
	lease  RuntimeLease
	socket SocketClient
	web    WebClient

	drainMu        sync.Mutex
	draining       bool
	drainRequested bool

	done                chan struct{}
	closeOnce           sync.Once
	disconnectOnce      sync.Once
	disconnectScheduled sync.Once
}

// runtimeSnapshot mirrors the Node fencing snapshot: the identities a flow
// re-validates after every await point.
type runtimeSnapshot struct {
	leaseID string
	socket  SocketClient
	web     WebClient
}

// NewAdapter applies defaults and returns a stopped adapter.
func NewAdapter(opts AdapterOptions) *Adapter {
	if opts.Logger == nil {
		opts.Logger = slog.New(slog.DiscardHandler)
	}
	if opts.DiscoveryEvery <= 0 {
		opts.DiscoveryEvery = 15 * time.Second
	}
	if opts.HeartbeatEvery <= 0 {
		opts.HeartbeatEvery = 15 * time.Second
	}
	if opts.DeliveryPoll <= 0 {
		opts.DeliveryPoll = time.Second
	}
	if opts.MaxInFlight <= 0 {
		opts.MaxInFlight = 8
	}
	if opts.TickerFactory == nil {
		opts.TickerFactory = func(d time.Duration) Ticker { return realTicker{time.NewTicker(d)} }
	}
	return &Adapter{
		opts:     opts,
		log:      opts.Logger.With("component", "adapter"),
		runtimes: map[string]*runtime{},
		sem:      make(chan struct{}, max(1, opts.MaxInFlight)),
		stopCh:   make(chan struct{}),
		stopDone: make(chan struct{}),
	}
}

// Start wires abort handling, runs the first discovery cycle synchronously,
// then leaves a discovery loop running until Stop or ctx cancellation.
func (a *Adapter) Start(ctx context.Context) error {
	a.mu.Lock()
	if a.stopped {
		a.mu.Unlock()
		return context.Canceled
	}
	if a.started {
		a.mu.Unlock()
		return errors.New("adapter was already started")
	}
	a.started = true
	a.baseCtx, a.cancelBase = context.WithCancel(context.WithoutCancel(ctx))
	a.wg.Add(1)
	a.mu.Unlock()
	defer a.wg.Done()
	go func() {
		select {
		case <-ctx.Done():
			a.Stop()
		case <-a.stopCh:
		}
	}()
	err := a.RefreshConnections(a.flowCtx())
	a.startDiscoveryLoop()
	return err
}

func (a *Adapter) startDiscoveryLoop() {
	if !a.beginWork() {
		return
	}
	go func() {
		defer a.wg.Done()
		ticker := a.opts.TickerFactory(floorDuration(a.opts.DiscoveryEvery, time.Second))
		defer ticker.Stop()
		for {
			select {
			case <-a.stopCh:
				return
			case <-ticker.C():
				if err := a.RefreshConnections(a.flowCtx()); err != nil && !a.isStopped() {
					a.log.Error("target discovery failed", "reason", SafeErrorMessage(err))
				}
			}
		}
	}()
}

// Stop disconnects every runtime and waits for in-flight flows to unwind.
func (a *Adapter) Stop() {
	a.mu.Lock()
	if a.stopped {
		done := a.stopDone
		a.mu.Unlock()
		<-done
		return
	}
	a.stopped = true
	cancelBase := a.cancelBase
	pending := make([]*runtime, 0, len(a.runtimes))
	for key, rt := range a.runtimes {
		delete(a.runtimes, key)
		pending = append(pending, rt)
	}
	a.mu.Unlock()
	defer close(a.stopDone)
	if cancelBase != nil {
		cancelBase()
	}
	close(a.stopCh)

	// Disconnect in parallel so per-socket bounds do not add up across targets.
	var disconnectWG sync.WaitGroup
	disconnectWG.Add(len(pending))
	for _, rt := range pending {
		go func(rt *runtime) {
			defer disconnectWG.Done()
			a.disconnectRuntime(rt)
		}(rt)
	}
	disconnectWG.Wait()
	if a.opts.Dispose != nil {
		a.disposeOnce.Do(a.opts.Dispose)
	}
	a.wg.Wait()
}

func (a *Adapter) isStopped() bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.stopped
}

func (a *Adapter) beginWork() bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.stopped {
		return false
	}
	a.wg.Add(1)
	return true
}

func (a *Adapter) flowCtx() context.Context {
	a.mu.Lock()
	ctx := a.baseCtx
	a.mu.Unlock()
	if ctx == nil {
		return context.Background()
	}
	return ctx
}

// RefreshConnections reconciles the served targets with discovery: vanished
// targets disconnect immediately, new ones go through validation probe and
// runtime lease acquisition concurrently; a failing target logs and skips
// without poisoning its siblings.
func (a *Adapter) RefreshConnections(ctx context.Context) error {
	if a.isStopped() || ctx.Err() != nil {
		return nil
	}
	targets, err := a.opts.Transport.Discover(ctx)
	if err != nil {
		return err
	}
	current := map[string]bool{}
	for _, ref := range targets {
		current[ref.Key()] = true
	}
	var vanished []*runtime
	a.mu.Lock()
	if a.stopped {
		a.mu.Unlock()
		return nil
	}
	for key, rt := range a.runtimes {
		if !current[key] {
			delete(a.runtimes, key)
			vanished = append(vanished, rt)
			a.wg.Add(1)
		}
	}
	a.mu.Unlock()
	for _, rt := range vanished {
		rt := rt
		go func() {
			defer a.wg.Done()
			a.disconnectRuntime(rt)
		}()
	}
	for _, ref := range targets {
		key := ref.Key()
		a.mu.Lock()
		if a.stopped {
			a.mu.Unlock()
			break
		}
		exists := a.runtimes[key] != nil
		if !exists {
			a.wg.Add(1)
		}
		a.mu.Unlock()
		if exists {
			continue
		}
		ref := ref
		go func() {
			defer a.wg.Done()
			if err := a.connectTarget(ctx, ref); err != nil {
				a.log.Error("target connection failed", "target", key, "reason", SafeErrorMessage(err))
			}
		}()
	}
	return nil
}

// connectTarget runs the validation probe and opens one runtime.
func (a *Adapter) connectTarget(ctx context.Context, ref Target) error {
	validation, err := a.opts.Transport.AcquireLease(ctx, ref, LeaseValidation, a.opts.AdapterID)
	if err != nil {
		return err
	}
	if validationLease, ok := validation.(ValidationLease); ok {
		if err := a.validateTarget(ctx, ref, validationLease); err != nil {
			return err
		}
	}
	lease, err := a.opts.Transport.AcquireLease(ctx, ref, LeaseRuntime, a.opts.AdapterID)
	if err != nil {
		return err
	}
	runtimeLease, ok := lease.(RuntimeLease)
	if !ok {
		return nil
	}

	rt := &runtime{
		key:    ref.Key(),
		target: ref,
		lease:  runtimeLease,
		done:   make(chan struct{}),
	}
	a.mu.Lock()
	if a.stopped || a.runtimes[rt.key] != nil {
		a.mu.Unlock()
		return nil
	}
	a.runtimes[rt.key] = rt
	a.mu.Unlock()
	if err := a.startRuntime(rt); err != nil {
		a.removeRuntime(rt)
		return err
	}
	return nil
}

// validateTarget opens one probe socket, verifies the presented app identity
// through the Server's hello report, and closes the probe. The hello outcome
// itself is the Server's decision; the adapter only reports what it saw.
func (a *Adapter) validateTarget(ctx context.Context, ref Target, lease ValidationLease) error {
	socket := a.opts.SocketFactory(lease.AppToken, ref)
	defer func() {
		_ = a.disconnectSocket(socket, ref)
	}()
	appID, err := socket.Start(ctx)
	if err != nil {
		return err
	}
	outcome, err := a.opts.Transport.ReportHello(ctx, ref, lease.LeaseID, appID)
	if err != nil {
		return err
	}
	// A stale probe can belong to a successor, so never let it acquire a
	// runtime lease and supersede the currently valid connection.
	switch outcome {
	case HelloLeaseStale, HelloAppIDMismatch:
		return errors.New("Slack hello validation rejected: " + string(outcome))
	default:
		return nil
	}
}

func (a *Adapter) startRuntime(rt *runtime) error {
	opened, err := a.openRuntimeSocket(rt)
	if err != nil {
		return err
	}
	if !opened {
		return nil
	}
	if !a.isActive(rt) {
		a.disconnectRuntime(rt)
		return nil
	}
	a.startTimerLoop(rt, floorDuration(a.opts.HeartbeatEvery, time.Second), func() { a.refresh(rt) })
	a.startTimerLoop(rt, floorDuration(a.opts.DeliveryPoll, 100*time.Millisecond), func() { _ = a.drain(rt) })
	// The first drain pass is awaited inside setup like the Node flow: a
	// failing initial drain fails this target's connection attempt.
	return a.drain(rt)
}

func (a *Adapter) openRuntimeSocket(rt *runtime) (bool, error) {
	ctx := a.flowCtx()
	web := a.opts.WebFactory(rt.lease.BotToken, rt.target)
	socket := a.opts.SocketFactory(rt.lease.AppToken, rt.target)
	if configurable, ok := socket.(interface{ SetMaxInFlight(int) }); ok {
		configurable.SetMaxInFlight(a.opts.MaxInFlight)
	}
	rt.mu.Lock()
	rt.web = web
	rt.socket = socket
	rt.mu.Unlock()
	a.observeSocket(socket, rt)
	socket.OnEvent(func(event SocketEvent) {
		if !a.beginWork() {
			return
		}
		defer a.wg.Done()
		a.onSocketEvent(rt, socket, event)
	})
	// The runtime socket's hello identity was already verified by the probe;
	// slack-go owns its handshake here.
	if _, err := socket.Start(ctx); err != nil {
		if !a.isActive(rt) {
			// Superseded while connecting: swallow quietly like Node does
			// when the generation check fails after start().
			_ = a.disconnectSocket(socket, rt.target)
			return false, nil
		}
		return false, err
	}
	if !a.isActive(rt) {
		_ = a.disconnectSocket(socket, rt.target)
		return false, nil
	}
	return true, nil
}

// onSocketEvent drops events from superseded sockets and forwards live ones.
// Concurrency belongs to the SocketClient implementation: it invokes this
// callback per event on its own pump goroutines, and the in-flight semaphore
// inside handleEvent bounds how many flows process at once.
func (a *Adapter) onSocketEvent(rt *runtime, socket SocketClient, event SocketEvent) {
	if !a.runtimeMatchesSocket(rt, socket) {
		return
	}
	interaction := IsSlackInteraction(event.Body)
	eventType := SlackEventType(event.Body)
	a.log.Info("envelope received", "target", rt.key, "event", eventType)
	ctx := event.Context
	if ctx == nil {
		ctx = a.flowCtx()
	}
	if err := a.handleEvent(ctx, rt, event.Body, event.Ack); err != nil {
		message := "event handling failed before acknowledgement"
		if interaction {
			message = "interaction processing failed before acknowledgement"
		}
		a.log.Error(message, "target", rt.key, "event", eventType, "reason", SafeErrorMessage(err))
	}
}

func (a *Adapter) refresh(rt *runtime) {
	ctx := a.flowCtx()
	if !a.isActive(rt) || ctx.Err() != nil {
		return
	}
	snapshot := a.snapshot(rt)
	if snapshot == nil {
		return
	}
	renewal, err := a.opts.Transport.RenewLease(ctx, rt.target, snapshot.leaseID, a.opts.AdapterID)
	if a.isStopped() || ctx.Err() != nil {
		return
	}
	if err != nil {
		a.log.Error("target lease refresh failed", "target", rt.key, "reason", SafeErrorMessage(err))
		a.removeRuntime(rt)
		return
	}
	if !a.isActive(rt) {
		return
	}
	if renewal == nil || renewal.Kind != LeaseRuntime || renewal.LeaseID != snapshot.leaseID {
		a.removeRuntime(rt)
		return
	}
	rt.mu.Lock()
	rt.lease.Generation = renewal.Generation
	rt.lease.ExpiresAt = renewal.ExpiresAt
	rt.mu.Unlock()
	_ = a.drain(rt)
}

// removeRuntime evicts a runtime only while the map still points at it, so
// a stale failure from a replaced runtime never deletes its successor. The
// old runtime itself always disconnects.
func (a *Adapter) removeRuntime(rt *runtime) {
	a.mu.Lock()
	if a.runtimes[rt.key] == rt {
		delete(a.runtimes, rt.key)
	}
	a.mu.Unlock()
	a.disconnectRuntime(rt)
}

func (a *Adapter) isActive(rt *runtime) bool {
	a.mu.Lock()
	active := !a.stopped && a.runtimes[rt.key] == rt
	a.mu.Unlock()
	return active
}

func (a *Adapter) runtimeMatchesSocket(rt *runtime, socket SocketClient) bool {
	rt.mu.Lock()
	matches := rt.socket == socket
	rt.mu.Unlock()
	return matches && a.isActive(rt)
}

func (a *Adapter) snapshot(rt *runtime) *runtimeSnapshot {
	if !a.isActive(rt) {
		return nil
	}
	rt.mu.Lock()
	defer rt.mu.Unlock()
	if rt.socket == nil || rt.web == nil {
		return nil
	}
	return &runtimeSnapshot{leaseID: rt.lease.LeaseID, socket: rt.socket, web: rt.web}
}

func (a *Adapter) isCurrent(rt *runtime, snapshot *runtimeSnapshot) bool {
	if snapshot == nil || !a.isActive(rt) {
		return false
	}
	rt.mu.Lock()
	defer rt.mu.Unlock()
	return rt.socket == snapshot.socket && rt.web == snapshot.web
}

// assertCurrent unwinds via StaleRuntimeError; it is used inline in flows
// that recover it and as the ensureCurrent callback handed to delivery code.
func (a *Adapter) assertCurrent(rt *runtime, snapshot *runtimeSnapshot) {
	if !a.isCurrent(rt, snapshot) {
		panic(StaleRuntimeError{})
	}
}

// observeSocket logs connection-state transitions when the client exposes them.
func (a *Adapter) observeSocket(socket SocketClient, rt *runtime) {
	stateful, ok := socket.(interface {
		OnState(handler func(state string, apiErr error))
	})
	if !ok {
		return
	}
	key := rt.key
	stateful.OnState(func(state string, apiErr error) {
		if state == "error" {
			reason := ""
			if apiErr != nil {
				reason = SafeErrorMessage(apiErr)
			}
			a.log.Error("socket failed", "target", key, "state", state, "reason", reason)
			a.removeRuntimeAfterSocketFailure(rt, socket)
			return
		}
		if apiErr != nil {
			a.log.Info("socket state changed", "target", key, "state", state, "reason", SafeErrorMessage(apiErr))
			return
		}
		a.log.Info("socket state changed", "target", key, "state", state)
	})
}

func (a *Adapter) removeRuntimeAfterSocketFailure(rt *runtime, socket SocketClient) {
	rt.mu.Lock()
	matches := rt.socket == socket
	rt.mu.Unlock()
	if !matches {
		return
	}
	a.mu.Lock()
	if a.stopped || a.runtimes[rt.key] != rt {
		a.mu.Unlock()
		return
	}
	delete(a.runtimes, rt.key)
	rt.closeOnce.Do(func() { close(rt.done) })
	scheduled := false
	rt.disconnectScheduled.Do(func() {
		a.wg.Add(1)
		scheduled = true
	})
	a.mu.Unlock()
	if scheduled {
		go func() {
			defer a.wg.Done()
			a.disconnectRuntime(rt)
		}()
	}
}

// handleEvent forwards one inbound body: interactions forward before
// acknowledging, messages forward before acknowledging; both trigger a drain.
// Fencing panics unwind into the deferred recovery and are swallowed.
func (a *Adapter) handleEvent(ctx context.Context, rt *runtime, body any, ack func()) (err error) {
	snapshot := a.snapshot(rt)
	if snapshot == nil {
		return nil
	}
	interaction := IsSlackInteraction(body)
	acquired := false
	defer func() {
		if acquired {
			<-a.sem
		}
	}()
	defer func() {
		if recovered := recover(); recovered != nil {
			if _, stale := recovered.(StaleRuntimeError); stale {
				err = nil
				return
			}
			panic(recovered)
		}
	}()

	select {
	case a.sem <- struct{}{}:
	case <-ctx.Done():
		return ctx.Err()
	}
	acquired = true
	a.assertCurrent(rt, snapshot)
	a.log.Info("envelope forwarding", "target", rt.key, "event", SlackEventType(body))

	if interaction {
		envelope, normalizeErr := NormalizeSlackInteraction(body)
		if normalizeErr != nil {
			return normalizeErr
		}
		// Keep the Slack acknowledgement last: cancellation while waiting for
		// the shared permit must leave the envelope retryable, and an accepted
		// acknowledgement must always follow a forwarded action.
		if err := ctx.Err(); err != nil {
			return err
		}
		a.assertCurrent(rt, snapshot)
		if _, err := a.opts.Transport.Interaction(ctx, rt.target, envelope, snapshot.leaseID, a.opts.AdapterID); err != nil {
			if IsLeaseStale(err) {
				a.removeRuntimeAsync(rt)
				return nil
			}
			return err
		}
		a.assertCurrent(rt, snapshot)
		if err := ctx.Err(); err != nil {
			return err
		}
		a.assertCurrent(rt, snapshot)
		a.log.Info("interaction forwarded", "target", rt.key, "event", SlackEventType(body))
		ack()
		_ = a.drainWithContext(ctx, rt)
		return nil
	}

	envelope, normalizeErr := NormalizeSocketEvent(body)
	if normalizeErr != nil {
		return normalizeErr
	}
	a.assertCurrent(rt, snapshot)
	result, ingressErr := a.opts.Transport.Ingress(ctx, rt.target, envelope, snapshot.leaseID, a.opts.AdapterID)
	if ingressErr != nil {
		if IsLeaseStale(ingressErr) {
			a.removeRuntimeAsync(rt)
			return nil
		}
		return ingressErr
	}
	a.assertCurrent(rt, snapshot)
	a.log.Info("ingress accepted",
		"target", rt.key,
		"event", SlackEventType(body),
		"kind", result.Kind,
		"responseOwner", string(result.ResponseOwner),
	)
	proceed, renderErr := a.renderUserFacingRejection(ctx, rt, snapshot, &envelope, &result)
	if renderErr != nil {
		return renderErr
	}
	if !proceed {
		return nil
	}
	a.assertCurrent(rt, snapshot)
	ack()
	a.assertCurrent(rt, snapshot)
	_ = a.drainWithContext(ctx, rt)
	return nil
}

// renderUserFacingRejection posts an adapter-owned rejection reason to the
// originating conversation before the event is acknowledged. It reports
// whether the caller may proceed to acknowledgement.
func (a *Adapter) renderUserFacingRejection(
	ctx context.Context,
	rt *runtime,
	snapshot *runtimeSnapshot,
	envelope *Envelope,
	result *IngressResult,
) (bool, error) {
	switch result.ResponseOwner {
	case ResponseOwnerNone, ResponseOwnerServer:
		return a.isCurrent(rt, snapshot), nil
	case ResponseOwnerAdapter:
	default:
		return false, nil
	}
	if result.Reason == nil || *result.Reason == "" {
		return false, nil
	}
	if !a.isCurrent(rt, snapshot) {
		return false, nil
	}
	input := PostMessageInput{
		Channel:  envelope.ConversationID,
		Text:     *result.Reason,
		ThreadTs: derefString(envelope.ThreadTs),
	}
	a.assertCurrent(rt, snapshot)
	ts, postErr := snapshot.web.PostMessage(ctx, input)
	a.assertCurrent(rt, snapshot)
	_, _ = ts, postErr //nolint:dogsled // identity of the notice is not tracked
	if postErr != nil {
		if code := SlackErrorCode(postErr); code != "" {
			return false, errors.New("Slack rejected the direct ingress response: " + code)
		}
		return false, postErr
	}
	return true, nil
}

// drainOutcome classifies how one drain pass ended, mirroring the Node
// outer-catch handling: stale failures swallow, stale leases evict, generic
// failures surface to the initial-drain caller and are logged elsewhere.
type drainOutcome struct {
	stale      bool
	leaseStale bool
	failure    error
}

func (o *drainOutcome) terminal() bool {
	return o.stale || o.leaseStale || o.failure != nil
}

func (o *drainOutcome) recordTransport(err error) {
	if err == nil {
		return
	}
	if IsLeaseStale(err) {
		o.leaseStale = true
		return
	}
	o.failure = err
}

// drain claims and settles deliveries single-flight per runtime: concurrent
// triggers coalesce through draining/drainRequested, uncertain recovery runs
// first, and the claim loop stops at the first null or uncertain outcome.
// The returned error is non-nil only for a generic failure, so the initial
// drain inside runtime setup can fail the connection like the Node await.
func (a *Adapter) drain(rt *runtime) error {
	return a.drainWithContext(a.flowCtx(), rt)
}

func (a *Adapter) drainWithContext(ctx context.Context, rt *runtime) error {
	snapshot := a.snapshot(rt)
	if snapshot == nil {
		return nil
	}
	rt.drainMu.Lock()
	if rt.draining {
		rt.drainRequested = true
		rt.drainMu.Unlock()
		return nil
	}
	rt.draining = true
	rt.drainMu.Unlock()

	outcome := a.runDrain(ctx, rt, snapshot)

	rt.drainMu.Lock()
	rt.draining = false
	requested := rt.drainRequested
	rt.drainRequested = false
	rt.drainMu.Unlock()

	switch {
	case outcome.leaseStale:
		a.removeRuntimeAsync(rt)
	case outcome.failure != nil:
		a.log.Error("delivery drain failed", "target", rt.key, "reason", SafeErrorMessage(outcome.failure))
	}
	if requested && a.isActive(rt) {
		_ = a.drainWithContext(ctx, rt)
	}
	return outcome.failure
}

// runDrain executes uncertain recovery followed by the claim loop. Only
// StaleRuntimeError panics cross into it — from the ensureCurrent callbacks
// inside delivery code — and the deferred recovery classifies them as stale.
func (a *Adapter) runDrain(ctx context.Context, rt *runtime, snapshot *runtimeSnapshot) (outcome drainOutcome) {
	defer func() {
		if recovered := recover(); recovered != nil {
			if _, ok := recovered.(StaleRuntimeError); ok {
				outcome.stale = true
				return
			}
			panic(recovered)
		}
	}()
	a.drainUncertain(ctx, rt, snapshot, &outcome)
	if outcome.terminal() {
		return outcome
	}
	for ctx.Err() == nil {
		if !a.isCurrent(rt, snapshot) {
			outcome.stale = true
			return outcome
		}
		delivery, err := a.opts.Transport.ClaimDelivery(ctx, rt.target, snapshot.leaseID, a.opts.AdapterID)
		if !a.isCurrent(rt, snapshot) {
			outcome.stale = true
			return outcome
		}
		if err != nil {
			outcome.recordTransport(err)
			return outcome
		}
		if delivery == nil {
			return outcome
		}
		if a.settleDelivery(ctx, rt, snapshot, delivery, &outcome) {
			return outcome
		}
	}
	return outcome
}

// settleDelivery performs one mutate+ack cycle. The Node inner try treats a
// fencing unwind as a silent stop and any other failure as an uncertain
// acknowledgement followed by continue; the uncertain acknowledgement's own
// failure surfaces through the outer classifier.
func (a *Adapter) settleDelivery(ctx context.Context, rt *runtime, snapshot *runtimeSnapshot, delivery *Delivery, outcome *drainOutcome) bool {
	var (
		ack       DeliveryAck
		mutateErr error
		fenced    bool
	)
	func() {
		defer func() {
			if recovered := recover(); recovered != nil {
				if _, stale := recovered.(StaleRuntimeError); stale {
					fenced = true
					return
				}
				panic(recovered)
			}
		}()
		a.assertCurrent(rt, snapshot)
		var err error
		ack, err = MutateDelivery(ctx, snapshot.web, delivery, func() { a.assertCurrent(rt, snapshot) })
		if err != nil {
			mutateErr = err
			return
		}
		a.assertCurrent(rt, snapshot)
		if ackErr := a.opts.Transport.AckDelivery(ctx, rt.target, WithAdapterID(ack, a.opts.AdapterID), snapshot.leaseID); ackErr != nil {
			mutateErr = ackErr
		}
	}()
	if fenced {
		outcome.stale = true
		return true
	}
	if mutateErr == nil {
		return false
	}
	return a.ackUncertain(ctx, rt, snapshot, delivery.ID, mutateErr, outcome)
}

// drainUncertain settles previously-uncertain deliveries through provider
// reconciliation before the claim loop; it stops at the first null claim or
// an outcome still reported uncertain.
func (a *Adapter) drainUncertain(ctx context.Context, rt *runtime, snapshot *runtimeSnapshot, outcome *drainOutcome) {
	for ctx.Err() == nil {
		if !a.isCurrent(rt, snapshot) {
			outcome.stale = true
			return
		}
		delivery, err := a.opts.Transport.ClaimUncertainDelivery(ctx, rt.target, snapshot.leaseID, a.opts.AdapterID)
		if !a.isCurrent(rt, snapshot) {
			outcome.stale = true
			return
		}
		if err != nil {
			outcome.recordTransport(err)
			return
		}
		if delivery == nil {
			return
		}

		var (
			ack          DeliveryAck
			reconcileErr error
			fenced       bool
		)
		func() {
			defer func() {
				if recovered := recover(); recovered != nil {
					if _, stale := recovered.(StaleRuntimeError); stale {
						fenced = true
						return
					}
					panic(recovered)
				}
			}()
			a.assertCurrent(rt, snapshot)
			var e error
			ack, e = Reconcile(ctx, snapshot.web, delivery, func() { a.assertCurrent(rt, snapshot) })
			if e != nil {
				reconcileErr = e
				return
			}
			a.assertCurrent(rt, snapshot)
		}()
		if fenced {
			outcome.stale = true
			return
		}
		if reconcileErr != nil {
			// The Node inner catch rethrows fencing errors to the outer
			// classifier and acknowledges anything else uncertain once.
			if IsLeaseStale(reconcileErr) {
				outcome.leaseStale = true
				return
			}
			a.ackUncertain(ctx, rt, snapshot, delivery.ID, reconcileErr, outcome)
			return
		}
		if !a.isCurrent(rt, snapshot) {
			outcome.stale = true
			return
		}
		if ackErr := a.opts.Transport.AckDelivery(ctx, rt.target, WithAdapterID(ack, a.opts.AdapterID), snapshot.leaseID); ackErr != nil {
			outcome.recordTransport(ackErr)
			return
		}
		if !a.isCurrent(rt, snapshot) {
			outcome.stale = true
			return
		}
		if ack.Outcome == OutcomeUncertain {
			return
		}
	}
}

// ackUncertain settles a failed delivery as uncertain. It reports whether
// the drain must stop afterwards: either the fencing check consumed the flow,
// or the uncertain acknowledgement itself failed terminally.
func (a *Adapter) ackUncertain(ctx context.Context, rt *runtime, snapshot *runtimeSnapshot, deliveryID string, cause error, outcome *drainOutcome) bool {
	if !a.isCurrent(rt, snapshot) {
		outcome.stale = true
		return true
	}
	err := a.opts.Transport.AckDelivery(ctx, rt.target, WithAdapterID(DeliveryAck{
		ID:      deliveryID,
		Outcome: OutcomeUncertain,
		Reason:  SafeErrorMessage(cause),
	}, a.opts.AdapterID), snapshot.leaseID)
	if !a.isCurrent(rt, snapshot) {
		outcome.stale = true
		return true
	}
	if err != nil {
		outcome.recordTransport(err)
		return true
	}
	return false
}

func (a *Adapter) disconnectRuntime(rt *runtime) {
	rt.closeOnce.Do(func() { close(rt.done) })
	rt.disconnectOnce.Do(func() {
		rt.mu.Lock()
		socket := rt.socket
		rt.mu.Unlock()
		_ = a.disconnectSocket(socket, rt.target)
	})
}

func (a *Adapter) removeRuntimeAsync(rt *runtime) {
	a.mu.Lock()
	if a.runtimes[rt.key] == rt {
		delete(a.runtimes, rt.key)
	}
	rt.closeOnce.Do(func() { close(rt.done) })
	scheduled := false
	if !a.stopped {
		rt.disconnectScheduled.Do(func() {
			a.wg.Add(1)
			scheduled = true
		})
	}
	a.mu.Unlock()
	if scheduled {
		go func() {
			defer a.wg.Done()
			a.disconnectRuntime(rt)
		}()
	}
}

func (a *Adapter) disconnectSocket(socket SocketClient, target Target) error {
	if socket == nil {
		return nil
	}
	ctx, cancel := context.WithTimeout(context.Background(), adapterDisconnectTimeout)
	defer cancel()
	if err := socket.Disconnect(ctx); err != nil {
		a.log.Error("socket disconnect failed", "target", target.Key(), "reason", SafeErrorMessage(err))
	}
	return nil
}

// startTimerLoop owns one ticker goroutine for a runtime; loops exit on
// adapter stop or runtime eviction.
func (a *Adapter) startTimerLoop(rt *runtime, every time.Duration, fn func()) {
	if !a.beginWork() {
		return
	}
	ticker := a.opts.TickerFactory(every)
	go func() {
		defer a.wg.Done()
		defer ticker.Stop()
		for {
			select {
			case <-a.stopCh:
				return
			case <-rt.done:
				return
			case <-ticker.C():
				fn()
			}
		}
	}()
}

func floorDuration(value, floor time.Duration) time.Duration {
	if value < floor {
		return floor
	}
	return value
}

// tokenShapePattern matches Slack token prefixes for redaction before any
// log line leaves the process.
var tokenShapePattern = regexp.MustCompile(`(?i)(?:xapp|xoxb|xoxp|xoxe)[.A-Za-z0-9_-]*`)

// SafeErrorMessage renders an error for logs with token shapes redacted.
func SafeErrorMessage(err error) string {
	if err == nil {
		return ""
	}
	return tokenShapePattern.ReplaceAllString(err.Error(), "<redacted>")
}
