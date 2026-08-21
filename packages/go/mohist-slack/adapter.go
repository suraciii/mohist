package mohistslack

import (
	"context"
	"errors"
	"log/slog"
	"sync"
	"time"
)

// ErrStaleRuntime reports that the runtime a callback captured was replaced
// or evicted while the callback ran. Superseded callbacks must stop silently.
var ErrStaleRuntime = errors.New("stale runtime")

// InboundEvent is one normalized Slack delivery from an EventSource. Exactly
// one of Message and Interaction is set; Ack settles the event with Slack.
type InboundEvent struct {
	Message     *Envelope
	Interaction *InteractionEnvelope
	Ack         func(context.Context) error
}

// EventSource is the live inbound side for one target: a Socket Mode
// connection in production, a fake in tests.
type EventSource interface {
	// Start connects and returns the Slack app identity observed on the wire.
	Start(ctx context.Context) (string, error)
	// Events streams inbound events until Close.
	Events() <-chan InboundEvent
	Close(ctx context.Context) error
}

// EventSourceFactory builds the event source for one target and token.
type EventSourceFactory func(ctx context.Context, target Target, appToken string) (EventSource, error)

// SlackClientFactory builds the Web API client for one bot token.
type SlackClientFactory func(botToken string) (SlackClient, error)

// Ticker is the injectable time source for periodic work.
type Ticker interface {
	C() <-chan time.Time
	Stop()
}

// TickerFactory builds one ticker per interval.
type TickerFactory func(d time.Duration) Ticker

// SystemTicker is the wall-clock ticker factory used in production.
func SystemTicker(d time.Duration) Ticker { return systemTicker{t: time.NewTicker(d)} }

type systemTicker struct{ t *time.Ticker }

func (s systemTicker) C() <-chan time.Time { return s.t.C }
func (s systemTicker) Stop()               { s.t.Stop() }

const (
	defaultDiscoveryInterval = 15 * time.Second
	defaultHeartbeatInterval = 15 * time.Second
	defaultDeliveryInterval  = 1 * time.Second
	defaultMaxInFlight       = 8
	minTickInterval          = time.Second
	minDeliveryInterval      = 100 * time.Millisecond
	backpressureDefaultText  = "This Slack Connection is backpressured; retry after pending deliveries drain."
)

// AdapterOptions configures an Adapter. Zero intervals fall back to the Node
// defaults with their floors; MaxInFlight defaults to 8.
type AdapterOptions struct {
	AdapterID            string
	Transport            *ServerAPI
	NewEventSource       EventSourceFactory
	NewSlackClient       SlackClientFactory
	DiscoveryInterval    time.Duration
	HeartbeatInterval    time.Duration
	DeliveryPollInterval time.Duration
	MaxInFlight          int
	TickerFactory        TickerFactory
	Logger               *slog.Logger
}

// Adapter owns one runtime per discovered target and drives the lease,
// heartbeat, ingress, and delivery state machine for each.
type Adapter struct {
	transport         *ServerAPI
	adapterID         string
	newSource         EventSourceFactory
	newClient         SlackClientFactory
	discoveryInterval time.Duration
	heartbeatInterval time.Duration
	deliveryInterval  time.Duration
	maxInFlight       int
	tickers           TickerFactory
	log               *slog.Logger
	ctx               context.Context
	cancel            context.CancelFunc
	wg                sync.WaitGroup
	sem               chan struct{}
	mu                sync.Mutex
	runtimes          map[string]*runtime
	connecting        map[string]bool
	stopWaiter        sync.Once
}

type runtime struct {
	target         Target
	lease          RuntimeLease
	generation     int
	source         EventSource
	client         SlackClient
	heartbeat      Ticker
	deliveryTicker Ticker
	draining       bool
	drainRequested bool
	closed         bool
}

type runtimeSnapshot struct {
	generation int
	lease      RuntimeLease
	source     EventSource
	client     SlackClient
}

// NewAdapter resolves option defaults and returns a stopped adapter.
func NewAdapter(options AdapterOptions) *Adapter {
	if options.DiscoveryInterval <= 0 {
		options.DiscoveryInterval = defaultDiscoveryInterval
	}
	if options.DiscoveryInterval < minTickInterval {
		options.DiscoveryInterval = minTickInterval
	}
	if options.HeartbeatInterval <= 0 {
		options.HeartbeatInterval = defaultHeartbeatInterval
	}
	if options.HeartbeatInterval < minTickInterval {
		options.HeartbeatInterval = minTickInterval
	}
	if options.DeliveryPollInterval <= 0 {
		options.DeliveryPollInterval = defaultDeliveryInterval
	}
	if options.DeliveryPollInterval < minDeliveryInterval {
		options.DeliveryPollInterval = minDeliveryInterval
	}
	if options.MaxInFlight <= 0 {
		options.MaxInFlight = defaultMaxInFlight
	}
	tickers := options.TickerFactory
	if tickers == nil {
		tickers = SystemTicker
	}
	logger := options.Logger
	if logger == nil {
		logger = slog.New(slog.DiscardHandler)
	}
	ctx, cancel := context.WithCancel(context.Background())
	return &Adapter{
		transport:         options.Transport,
		adapterID:         options.AdapterID,
		newSource:         options.NewEventSource,
		newClient:         options.NewSlackClient,
		discoveryInterval: options.DiscoveryInterval,
		heartbeatInterval: options.HeartbeatInterval,
		deliveryInterval:  options.DeliveryPollInterval,
		maxInFlight:       options.MaxInFlight,
		tickers:           tickers,
		log:               logger.With("component", "slack-adapter"),
		ctx:               ctx,
		cancel:            cancel,
		sem:               make(chan struct{}, options.MaxInFlight),
		runtimes:          make(map[string]*runtime),
		connecting:        make(map[string]bool),
	}
}

// Start begins the discovery loop. It returns immediately; Stop tears
// everything down.
func (a *Adapter) Start() {
	a.mu.Lock()
	defer a.mu.Unlock()
	a.wg.Add(1)
	go a.discoveryLoop()
}

// Stop cancels every loop, closes every event source, and waits for them.
func (a *Adapter) Stop() {
	a.stopWaiter.Do(func() {
		a.cancel()
		a.mu.Lock()
		stale := make([]*runtime, 0, len(a.runtimes))
		for _, rt := range a.runtimes {
			stale = append(stale, rt)
		}
		a.runtimes = make(map[string]*runtime)
		a.connecting = make(map[string]bool)
		a.mu.Unlock()
		for _, rt := range stale {
			a.teardown(rt)
		}
		a.wg.Wait()
	})
}

func (a *Adapter) discoveryLoop() {
	defer a.wg.Done()
	a.refreshConnections()
	ticker := a.tickers(a.discoveryInterval)
	defer ticker.Stop()
	for {
		select {
		case <-a.ctx.Done():
			return
		case <-ticker.C():
			a.refreshConnections()
		}
	}
}

// refreshConnections converges the runtime map onto the discovery result.
// One failing target is logged and skipped; siblings proceed.
func (a *Adapter) refreshConnections() {
	targets, err := a.transport.Discover(a.ctx)
	if err != nil {
		a.log.Error("target discovery failed", "reason", RedactTokens(err.Error()))
		return
	}
	current := make(map[string]bool, len(targets))
	for _, target := range targets {
		current[target.Key()] = true
	}
	a.mu.Lock()
	var removed []*runtime
	for key, rt := range a.runtimes {
		if !current[key] {
			delete(a.runtimes, key)
			removed = append(removed, rt)
		}
	}
	var fresh []Target
	for _, target := range targets {
		key := target.Key()
		if _, exists := a.runtimes[key]; exists {
			continue
		}
		if a.connecting[key] {
			continue
		}
		a.connecting[key] = true
		fresh = append(fresh, target)
	}
	a.mu.Unlock()
	for _, rt := range removed {
		a.teardown(rt)
	}
	for _, target := range fresh {
		target := target
		a.wg.Add(1)
		go func() {
			defer a.wg.Done()
			a.connectTarget(target)
		}()
	}
}

// connectTarget walks one target through validation, hello, and the runtime
// lease, then starts its pumps. Every failure is contained to the target.
func (a *Adapter) connectTarget(target Target) {
	key := target.Key()
	defer func() {
		a.mu.Lock()
		delete(a.connecting, key)
		a.mu.Unlock()
	}()
	if a.lookup(key) != nil {
		return
	}
	validation, err := a.transport.AcquireLease(a.ctx, target, LeaseValidation, a.adapterID)
	if err != nil {
		a.log.Error("validation lease failed", "target", key, "reason", RedactTokens(err.Error()))
		return
	}
	if validation == nil {
		return
	}
	vlease, ok := validation.(ValidationLease)
	if !ok {
		return
	}
	probe, err := a.newSource(a.ctx, target, vlease.AppToken)
	if err != nil {
		a.log.Error("probe source failed", "target", key, "reason", RedactTokens(err.Error()))
		return
	}
	appID, err := probe.Start(a.ctx)
	if err != nil {
		a.closeSource(probe, key)
		a.log.Error("probe socket failed", "target", key, "reason", RedactTokens(err.Error()))
		return
	}
	// The hello outcome is reported but not branched on, matching the Node
	// implementation: the Server refuses the runtime lease when the identity
	// did not verify.
	if _, err := a.transport.ReportHello(a.ctx, target, vlease.LeaseID, appID); err != nil {
		a.closeSource(probe, key)
		a.log.Error("hello report failed", "target", key, "reason", RedactTokens(err.Error()))
		return
	}
	if err := probe.Close(a.ctx); err != nil {
		a.log.Error("probe close failed", "target", key, "reason", RedactTokens(err.Error()))
	}
	lease, err := a.transport.AcquireLease(a.ctx, target, LeaseRuntime, a.adapterID)
	if err != nil {
		a.log.Error("runtime lease failed", "target", key, "reason", RedactTokens(err.Error()))
		return
	}
	if lease == nil {
		return
	}
	runtimeLease, ok := lease.(RuntimeLease)
	if !ok {
		return
	}
	rt := &runtime{target: target, lease: runtimeLease}
	if !a.insert(key, rt) {
		return
	}
	source, err := a.newSource(a.ctx, target, runtimeLease.AppToken)
	if err != nil {
		a.removeRuntime(rt)
		a.log.Error("event source failed", "target", key, "reason", RedactTokens(err.Error()))
		return
	}
	if _, err := source.Start(a.ctx); err != nil {
		a.closeSource(source, key)
		a.removeRuntime(rt)
		a.log.Error("socket start failed", "target", key, "reason", RedactTokens(err.Error()))
		return
	}
	client, err := a.newClient(runtimeLease.BotToken)
	if err != nil {
		a.closeSource(source, key)
		a.removeRuntime(rt)
		a.log.Error("web client failed", "target", key, "reason", RedactTokens(err.Error()))
		return
	}
	a.mu.Lock()
	if a.runtimes[key] != rt || a.ctx.Err() != nil {
		a.mu.Unlock()
		a.closeSource(source, key)
		return
	}
	rt.source = source
	rt.client = client
	rt.heartbeat = a.tickers(a.heartbeatInterval)
	rt.deliveryTicker = a.tickers(a.deliveryInterval)
	a.mu.Unlock()

	a.wg.Add(3)
	go a.eventPump(rt)
	go a.tickLoop(rt, rt.heartbeat, a.refresh)
	go a.tickLoop(rt, rt.deliveryTicker, a.drain)
	a.drain(rt)
	a.log.Info("runtime started", "target", key)
}

func (a *Adapter) lookup(key string) *runtime {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.runtimes[key]
}

func (a *Adapter) insert(key string, rt *runtime) bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	if _, exists := a.runtimes[key]; exists || a.ctx.Err() != nil {
		return false
	}
	a.runtimes[key] = rt
	return true
}

func (a *Adapter) removeRuntime(rt *runtime) {
	a.mu.Lock()
	if a.runtimes[rt.target.Key()] == rt {
		delete(a.runtimes, rt.target.Key())
	}
	a.mu.Unlock()
	a.teardown(rt)
}

// teardown stops timers and closes the source exactly once. Only the runtime
// the map still points at may be evicted by callers; superseded runtimes are
// always disconnected either way.
func (a *Adapter) teardown(rt *runtime) {
	a.mu.Lock()
	if rt.closed {
		a.mu.Unlock()
		return
	}
	rt.closed = true
	heartbeat, delivery, source := rt.heartbeat, rt.deliveryTicker, rt.source
	rt.heartbeat, rt.deliveryTicker, rt.source = nil, nil, nil
	a.mu.Unlock()
	if heartbeat != nil {
		heartbeat.Stop()
	}
	if delivery != nil {
		delivery.Stop()
	}
	if source != nil {
		a.closeSource(source, rt.target.Key())
	}
}

func (a *Adapter) closeSource(source EventSource, key string) {
	if err := source.Close(a.ctx); err != nil {
		a.log.Error("socket disconnect failed", "target", key, "reason", RedactTokens(err.Error()))
	}
}

func (a *Adapter) snapshot(rt *runtime) (runtimeSnapshot, bool) {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.runtimes[rt.target.Key()] != rt || rt.source == nil || rt.client == nil || rt.closed {
		return runtimeSnapshot{}, false
	}
	return runtimeSnapshot{
		generation: rt.generation,
		lease:      rt.lease,
		source:     rt.source,
		client:     rt.client,
	}, true
}

func (a *Adapter) ensureCurrent(rt *runtime, snap runtimeSnapshot) error {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.runtimes[rt.target.Key()] != rt ||
		rt.generation != snap.generation ||
		rt.source != snap.source ||
		rt.client != snap.client ||
		rt.closed {
		return ErrStaleRuntime
	}
	return nil
}

func (a *Adapter) eventPump(rt *runtime) {
	defer a.wg.Done()
	snap, ok := a.snapshot(rt)
	if !ok {
		return
	}
	for {
		select {
		case <-a.ctx.Done():
			return
		case event, open := <-snap.source.Events():
			if !open {
				return
			}
			// The slot is taken before dispatch so the pump applies
			// backpressure to the source instead of spawning unbounded
			// handlers; processing itself runs concurrently, matching the
			// Node adapter's socket dispatch.
			if err := a.acquireSlot(); err != nil {
				return
			}
			a.wg.Add(1)
			go func(event InboundEvent) {
				defer a.wg.Done()
				defer a.releaseSlot()
				a.handleEvent(rt, event)
			}(event)
		}
	}
}

func (a *Adapter) tickLoop(rt *runtime, ticker Ticker, action func(*runtime)) {
	defer a.wg.Done()
	for {
		select {
		case <-a.ctx.Done():
			return
		case <-ticker.C():
			action(rt)
		}
	}
}

func (a *Adapter) handleEvent(rt *runtime, event InboundEvent) {
	snap, ok := a.snapshot(rt)
	if !ok {
		return
	}
	interaction := event.Interaction != nil
	if interaction {
		if err := a.ensureCurrent(rt, snap); err != nil {
			return
		}
		if err := event.Ack(a.ctx); err != nil {
			a.log.Error("interaction acknowledgement failed",
				"target", rt.target.Key(), "reason", RedactTokens(err.Error()))
			return
		}
		if err := a.ensureCurrent(rt, snap); err != nil {
			return
		}
	}
	if err := a.ensureCurrent(rt, snap); err != nil {
		return
	}
	if interaction {
		_, err := a.transport.Interaction(a.ctx, rt.target, *event.Interaction, snap.lease.LeaseID, a.adapterID)
		if !a.triage(rt, err, "interaction forwarding failed") {
			return
		}
		if err := a.ensureCurrent(rt, snap); err != nil {
			return
		}
		a.drain(rt)
		return
	}
	result, err := a.transport.Ingress(a.ctx, rt.target, *event.Message, snap.lease.LeaseID, a.adapterID)
	if !a.triage(rt, err, "ingress failed") {
		return
	}
	if err := a.ensureCurrent(rt, snap); err != nil {
		return
	}
	a.log.Info("ingress accepted", "target", rt.target.Key(), "kind", result.Kind)
	if !a.renderBackpressureNotice(rt, snap, *event.Message, result) {
		return
	}
	if err := a.ensureCurrent(rt, snap); err != nil {
		return
	}
	if err := event.Ack(a.ctx); err != nil {
		a.log.Error("acknowledgement failed", "target", rt.target.Key(), "reason", RedactTokens(err.Error()))
		return
	}
	if err := a.ensureCurrent(rt, snap); err != nil {
		return
	}
	a.drain(rt)
}

// triage contains one transport failure. It reports whether processing may
// continue; stale failures are swallowed and stale leases evict the runtime.
func (a *Adapter) triage(rt *runtime, err error, message string) bool {
	if err == nil {
		return true
	}
	if errors.Is(err, ErrStaleRuntime) {
		return false
	}
	var apiErr *APIError
	if errors.As(err, &apiErr) && apiErr.StaleLease() {
		a.log.Error("runtime lease went stale", "target", rt.target.Key())
		a.removeRuntime(rt)
		return false
	}
	a.log.Error(message, "target", rt.target.Key(), "reason", RedactTokens(err.Error()))
	return false
}

func (a *Adapter) acquireSlot() error {
	select {
	case a.sem <- struct{}{}:
		return nil
	case <-a.ctx.Done():
		return a.ctx.Err()
	}
}

func (a *Adapter) releaseSlot() { <-a.sem }

// renderBackpressureNotice tells the sender why their message was not
// accepted. It reports whether processing may continue toward the ack.
func (a *Adapter) renderBackpressureNotice(rt *runtime, snap runtimeSnapshot, envelope Envelope, result IngressResult) bool {
	if result.Kind != "backpressured" {
		return a.isCurrent(rt, snap)
	}
	a.mu.Lock()
	draining := rt.draining
	a.mu.Unlock()
	if draining {
		return a.isCurrent(rt, snap)
	}
	if !a.isCurrent(rt, snap) {
		return false
	}
	text := backpressureDefaultText
	if result.Reason != nil && *result.Reason != "" {
		text = *result.Reason
	}
	opts := PostOptions{}
	if envelope.ThreadTs != nil {
		opts.ThreadTs = *envelope.ThreadTs
	}
	if err := a.ensureCurrent(rt, snap); err != nil {
		return false
	}
	if _, err := snap.client.PostMessage(a.ctx, envelope.ConversationID, text, opts); err != nil {
		a.log.Error("backpressure notice failed", "target", rt.target.Key(), "reason", RedactTokens(err.Error()))
		return false
	}
	return a.isCurrent(rt, snap)
}

func (a *Adapter) isCurrent(rt *runtime, snap runtimeSnapshot) bool {
	return a.ensureCurrent(rt, snap) == nil
}

// isMember reports whether the runtime is still the tracked runtime for its
// target, regardless of generation.
func (a *Adapter) isMember(rt *runtime) bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.runtimes[rt.target.Key()] == rt
}

// refresh renews the runtime lease on the heartbeat tick.
func (a *Adapter) refresh(rt *runtime) {
	snap, ok := a.snapshot(rt)
	if !ok {
		return
	}
	generation := snap.generation
	renewal, err := a.transport.RenewLease(a.ctx, rt.target, snap.lease.LeaseID, a.adapterID)
	if a.ctx.Err() != nil {
		return
	}
	if err != nil {
		a.log.Error("target lease refresh failed",
			"target", rt.target.Key(), "reason", RedactTokens(err.Error()))
		a.removeRuntime(rt)
		return
	}
	a.mu.Lock()
	if a.runtimes[rt.target.Key()] != rt || rt.generation != generation {
		a.mu.Unlock()
		return
	}
	a.mu.Unlock()
	if renewal == nil || renewal.Kind != LeaseRuntime || renewal.LeaseID != snap.lease.LeaseID {
		a.removeRuntime(rt)
		return
	}
	a.mu.Lock()
	rt.lease.Generation = renewal.Generation
	rt.lease.ExpiresAt = renewal.ExpiresAt
	a.mu.Unlock()
	a.drain(rt)
}

// drain claims and settles pending deliveries. Concurrent triggers coalesce:
// a drain already running sets a flag and the next pass happens on finish.
func (a *Adapter) drain(rt *runtime) {
	snap, ok := a.snapshot(rt)
	if !ok {
		return
	}
	a.mu.Lock()
	if rt.draining {
		rt.drainRequested = true
		a.mu.Unlock()
		return
	}
	rt.draining = true
	a.mu.Unlock()
	defer func() {
		a.mu.Lock()
		rt.draining = false
		requested := rt.drainRequested
		rt.drainRequested = false
		a.mu.Unlock()
		// Membership, not generation: a renewal that bumped the generation
		// while this pass ran must not swallow a requested rerun.
		if requested && a.isMember(rt) {
			a.drain(rt)
		}
	}()
	if err := a.drainUncertain(rt, snap); err != nil {
		return
	}
	if err := a.ensureCurrent(rt, snap); err != nil {
		return
	}
	for a.ctx.Err() == nil {
		if err := a.ensureCurrent(rt, snap); err != nil {
			return
		}
		delivery, err := a.transport.ClaimDelivery(a.ctx, rt.target, snap.lease.LeaseID, a.adapterID)
		if !a.triage(rt, err, "delivery claim failed") {
			return
		}
		if err := a.ensureCurrent(rt, snap); err != nil {
			return
		}
		if delivery == nil {
			break
		}
		ack, err := MutateDelivery(a.ctx, snap.client, delivery, func() error {
			return a.ensureCurrent(rt, snap)
		})
		if err != nil {
			if errors.Is(err, ErrStaleRuntime) {
				return
			}
			if !a.settleUncertain(rt, snap, delivery, err) {
				return
			}
			continue
		}
		if err := a.ensureCurrent(rt, snap); err != nil {
			return
		}
		if err := a.transport.AckDelivery(a.ctx, rt.target, WithAdapterID(ack, a.adapterID), snap.lease.LeaseID); !a.triage(rt, err, "delivery ack failed") {
			return
		}
		if err := a.ensureCurrent(rt, snap); err != nil {
			return
		}
	}
}

// drainUncertain replays deliveries whose earlier settlement was unknown. It
// stops after the first remaining uncertainty.
func (a *Adapter) drainUncertain(rt *runtime, snap runtimeSnapshot) error {
	for a.ctx.Err() == nil {
		if err := a.ensureCurrent(rt, snap); err != nil {
			return err
		}
		delivery, err := a.transport.ClaimUncertainDelivery(a.ctx, rt.target, snap.lease.LeaseID, a.adapterID)
		if !a.triage(rt, err, "uncertain claim failed") {
			return nil
		}
		if err := a.ensureCurrent(rt, snap); err != nil {
			return err
		}
		if delivery == nil {
			return nil
		}
		ack, err := ReconcileDelivery(a.ctx, snap.client, delivery, func() error {
			return a.ensureCurrent(rt, snap)
		})
		if err != nil && !errors.Is(err, ErrStaleRuntime) {
			apiErr := (*APIError)(nil)
			if errors.As(err, &apiErr) && apiErr.StaleLease() {
				return err
			}
			ack = uncertainAck(delivery, RedactTokens(err.Error()))
			err = nil
		}
		if err != nil {
			return err
		}
		if err := a.ensureCurrent(rt, snap); err != nil {
			return err
		}
		if err := a.transport.AckDelivery(a.ctx, rt.target, WithAdapterID(ack, a.adapterID), snap.lease.LeaseID); !a.triage(rt, err, "uncertain ack failed") {
			return nil
		}
		if err := a.ensureCurrent(rt, snap); err != nil {
			return err
		}
		if ack.Outcome == OutcomeUncertain {
			return nil
		}
	}
	return nil
}

// settleUncertain reports a mutation failure as an uncertain outcome. It
// returns whether processing may continue with the next delivery.
func (a *Adapter) settleUncertain(rt *runtime, snap runtimeSnapshot, delivery *Delivery, cause error) bool {
	ack := uncertainAck(delivery, RedactTokens(cause.Error()))
	if err := a.ensureCurrent(rt, snap); err != nil {
		return false
	}
	if err := a.transport.AckDelivery(a.ctx, rt.target, WithAdapterID(ack, a.adapterID), snap.lease.LeaseID); !a.triage(rt, err, "uncertain settle failed") {
		return false
	}
	return a.ensureCurrent(rt, snap) == nil
}

// runtimeLeaseOf exposes the current lease for assertions in tests.
func (a *Adapter) runtimeLeaseOf(key string) (RuntimeLease, bool) {
	a.mu.Lock()
	defer a.mu.Unlock()
	rt, ok := a.runtimes[key]
	if !ok {
		return RuntimeLease{}, false
	}
	return rt.lease, true
}

// runtimeCount reports how many runtimes are currently tracked.
func (a *Adapter) runtimeCount() int {
	a.mu.Lock()
	defer a.mu.Unlock()
	return len(a.runtimes)
}
