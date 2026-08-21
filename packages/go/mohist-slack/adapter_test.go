package mohistslack

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"
)

// eventLog records ordered markers from transport handlers, fake clients, and
// ack callbacks. wait() is channel-driven with a bounded test-side timeout.
type eventLog struct {
	mu     sync.Mutex
	items  []string
	seqs   map[string]int
	notify chan struct{}
}

func newEventLog() *eventLog {
	return &eventLog{seqs: map[string]int{}, notify: make(chan struct{}, 256)}
}

func (l *eventLog) add(item string) {
	l.mu.Lock()
	l.items = append(l.items, item)
	l.mu.Unlock()
	select {
	case l.notify <- struct{}{}:
	default:
	}
}

func (l *eventLog) routeHit(route string) int {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.seqs[route]++
	item := fmt.Sprintf("%s#%d", route, l.seqs[route])
	l.items = append(l.items, item)
	select {
	case l.notify <- struct{}{}:
	default:
	}
	return l.seqs[route]
}

func (l *eventLog) indexOf(substr string) int {
	l.mu.Lock()
	defer l.mu.Unlock()
	for index, item := range l.items {
		if strings.Contains(item, substr) {
			return index
		}
	}
	return -1
}

func (l *eventLog) count(substr string) int {
	l.mu.Lock()
	defer l.mu.Unlock()
	total := 0
	for _, item := range l.items {
		if strings.Contains(item, substr) {
			total++
		}
	}
	return total
}

func (l *eventLog) wait(t *testing.T, substr string) {
	t.Helper()
	for {
		if l.indexOf(substr) >= 0 {
			return
		}
		select {
		case <-l.notify:
		case <-time.After(2 * time.Second):
			t.Fatalf("timed out waiting for %q; log = %v", substr, l.snapshot())
		}
	}
}

func (l *eventLog) snapshot() []string {
	l.mu.Lock()
	defer l.mu.Unlock()
	return append([]string(nil), l.items...)
}

// fakeSource is a controllable EventSource recording its lifecycle.
type fakeSource struct {
	mu          sync.Mutex
	appID       string
	startCount  int
	closeCount  int
	events      chan InboundEvent
	closeSignal chan string // optional; receives the source key on Close
	key         string
}

func newFakeSource(key, appID string) *fakeSource {
	return &fakeSource{key: key, appID: appID, events: make(chan InboundEvent, 16)}
}

func (s *fakeSource) Start(_ context.Context) (string, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.startCount++
	return s.appID, nil
}

func (s *fakeSource) Events() <-chan InboundEvent { return s.events }

func (s *fakeSource) Close(_ context.Context) error {
	s.mu.Lock()
	s.closeCount++
	signal := s.closeSignal
	key := s.key
	s.mu.Unlock()
	if signal != nil {
		select {
		case signal <- key:
		default:
		}
	}
	return nil
}

func (s *fakeSource) emit(t *testing.T, event InboundEvent) {
	t.Helper()
	select {
	case s.events <- event:
	case <-time.After(2 * time.Second):
		t.Fatalf("emit timed out")
	}
}

func (s *fakeSource) startCalls() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.startCount
}

func (s *fakeSource) closeCalls() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.closeCount
}

// sourceFactory builds one fake source per creation attempt.
type sourceFactory struct {
	mu      sync.Mutex
	created map[string][]*fakeSource
	make    func(target Target, attempt int) *fakeSource
}

func newSourceFactory(make func(target Target, attempt int) *fakeSource) *sourceFactory {
	return &sourceFactory{created: map[string][]*fakeSource{}, make: make}
}

func (f *sourceFactory) factory() EventSourceFactory {
	return func(_ context.Context, target Target, appToken string) (EventSource, error) {
		f.mu.Lock()
		attempt := len(f.created[target.Key()]) + 1
		source := f.make(target, attempt)
		f.created[target.Key()] = append(f.created[target.Key()], source)
		f.mu.Unlock()
		return source, nil
	}
}

func (f *sourceFactory) all(key string) []*fakeSource {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]*fakeSource(nil), f.created[key]...)
}

func (f *sourceFactory) last(key string) *fakeSource {
	f.mu.Lock()
	defer f.mu.Unlock()
	created := f.created[key]
	if len(created) == 0 {
		return nil
	}
	return created[len(created)-1]
}

// clientFactory records bot tokens and exposes the clients for assertions.
type clientFactory struct {
	mu      sync.Mutex
	tokens  []string
	clients []*fakeSlack
	make    func(attempt int) *fakeSlack
}

func (f *clientFactory) factory() SlackClientFactory {
	return func(botToken string) (SlackClient, error) {
		f.mu.Lock()
		defer f.mu.Unlock()
		client := &fakeSlack{}
		if f.make != nil {
			client = f.make(len(f.clients) + 1)
		}
		f.tokens = append(f.tokens, botToken)
		f.clients = append(f.clients, client)
		return client, nil
	}
}

func (f *clientFactory) tokensList() []string {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]string(nil), f.tokens...)
}

func (f *clientFactory) last() *fakeSlack {
	f.mu.Lock()
	defer f.mu.Unlock()
	if len(f.clients) == 0 {
		return nil
	}
	return f.clients[len(f.clients)-1]
}

// manualTicker is driven by tests instead of the wall clock.
type manualTicker struct {
	c chan time.Time
}

func newManualTicker() *manualTicker { return &manualTicker{c: make(chan time.Time, 8)} }

func (m *manualTicker) C() <-chan time.Time { return m.c }
func (m *manualTicker) Stop()               {}

func (m *manualTicker) tick() {
	select {
	case m.c <- time.Now():
	default:
	}
}

type tickerSet struct {
	mu  sync.Mutex
	all []*manualTicker
}

func (s *tickerSet) factory(d time.Duration) Ticker {
	s.mu.Lock()
	defer s.mu.Unlock()
	ticker := newManualTicker()
	s.all = append(s.all, ticker)
	return ticker
}

func (s *tickerSet) tick(index int) {
	s.mu.Lock()
	ticker := s.all[index]
	s.mu.Unlock()
	ticker.tick()
}

const (
	vleaseJSON = `{"success":true,"data":{"leaseId":"v1","appToken":"xapp-v","expectedAppId":"A1","expiresAt":"2026-01-01T00:00:00Z","generation":1}}`
	rleaseJSON = `{"success":true,"data":{"leaseId":"r1","appToken":"xapp-r","botToken":"xoxb-r","expiresAt":"2026-01-01T00:00:00Z","generation":5}}`
)

// routeOverride customizes one route by suffix match. It receives the
// per-route sequence number and the decoded request body.
type routeOverride func(seq int, body map[string]any) (int, string)

func defaultRoute(route string, seq int, body map[string]any) (int, string) {
	switch {
	case route == "GET /api/slack-adapter/leases/targets":
		return 200, `{"success":true,"data":[{"kind":"connection","projectId":"p","connectionId":"c"}]}`
	case route == "POST /api/slack-adapter/leases/acquire":
		if body["kind"] == string(LeaseValidation) {
			return 200, vleaseJSON
		}
		return 200, rleaseJSON
	case route == "POST /api/slack-adapter/leases/hello":
		return 200, `{"success":true,"data":{"outcome":"verified"}}`
	case route == "POST /api/slack-adapter/leases/renew":
		return 200, fmt.Sprintf(`{"success":true,"data":{"leaseId":"r1","kind":"runtime","generation":%d,"expiresAt":"2026-01-02T00:00:00Z"}}`, 5+seq)
	case strings.HasSuffix(route, "/deliveries/claim"),
		strings.HasSuffix(route, "/deliveries/claim-uncertain"),
		strings.HasSuffix(route, "/deliveries/ack"):
		return 200, `{"success":true,"data":null}`
	case strings.HasSuffix(route, "/ingress"):
		return 200, `{"success":true,"data":{"kind":"accepted"}}`
	case strings.HasSuffix(route, "/interactions"):
		return 200, `{"success":true,"data":{"state":"ok"}}`
	default:
		return 200, `{"success":true,"data":null}`
	}
}

type adapterFixture struct {
	adapter *Adapter
	sources *sourceFactory
	clients *clientFactory
	tickers *tickerSet
	log     *eventLog
}

func newTestAdapter(t *testing.T, log *eventLog, overrides map[string]routeOverride, customize func(*AdapterOptions)) *adapterFixture {
	t.Helper()
	set := &tickerSet{}
	sources := newSourceFactory(func(target Target, attempt int) *fakeSource {
		return newFakeSource(target.Key(), "A1")
	})
	clients := &clientFactory{}
	match := func(route string) routeOverride {
		for suffix, handler := range overrides {
			if route == suffix || strings.HasSuffix(route, suffix) {
				return handler
			}
		}
		return nil
	}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		raw, _ := io.ReadAll(r.Body)
		var body map[string]any
		_ = json.Unmarshal(raw, &body)
		route := r.Method + " " + requestPath(r)
		seq := log.routeHit(route)
		status, response := defaultRoute(route, seq, body)
		if handler := match(route); handler != nil {
			status, response = handler(seq, body)
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(status)
		_, _ = w.Write([]byte(response))
	}))
	t.Cleanup(server.Close)
	api, err := NewServerAPI(server.URL, "tok", "op")
	if err != nil {
		t.Fatalf("NewServerAPI() error = %v", err)
	}
	options := AdapterOptions{
		AdapterID:            "a1",
		Transport:            api,
		NewEventSource:       sources.factory(),
		NewSlackClient:       clients.factory(),
		TickerFactory:        set.factory,
		DeliveryPollInterval: defaultDeliveryInterval,
	}
	if customize != nil {
		customize(&options)
	}
	adapter := NewAdapter(options)
	t.Cleanup(adapter.Stop)
	return &adapterFixture{adapter: adapter, sources: sources, clients: clients, tickers: set, log: log}
}

func messageEvent(sequence int) InboundEvent {
	return InboundEvent{
		Message: &Envelope{
			EventType:        "message",
			APIAppID:         "A1",
			TeamID:           "T123",
			ConversationID:   "C1",
			MessageTs:        fmt.Sprintf("1710000000.%06d", sequence),
			SenderKind:       SenderHuman,
			MentionedUserIDs: []string{},
			Files:            []FileRef{},
		},
		Ack: func(context.Context) error { return nil },
	}
}

func TestAdapterFullLifecycleAndInitialDrain(t *testing.T) {
	log := newEventLog()
	helloApps := make(chan any, 4)
	fx := newTestAdapter(t, log, map[string]routeOverride{
		"POST /api/slack-adapter/leases/hello": func(seq int, body map[string]any) (int, string) {
			helloApps <- body["appId"]
			return 200, `{"success":true,"data":{"outcome":"verified"}}`
		},
	}, nil)
	fx.adapter.Start()

	fx.log.wait(t, "POST /api/slack-adapter/leases/hello#1")
	fx.log.wait(t, "POST /api/projects/p/slack-connections/c/deliveries/claim#1")

	select {
	case appID := <-helloApps:
		if appID != "A1" {
			t.Fatalf("hello appId = %v", appID)
		}
	case <-time.After(2 * time.Second):
		t.Fatalf("hello appId not captured")
	}

	all := fx.sources.all("connection:p:c")
	if len(all) != 2 {
		t.Fatalf("sources created = %d, want probe + runtime", len(all))
	}
	if all[0].closeCalls() != 1 {
		t.Fatalf("probe close calls = %d, want 1", all[0].closeCalls())
	}
	if all[1].startCalls() != 1 {
		t.Fatalf("runtime source starts = %d", all[1].startCalls())
	}
	tokens := fx.clients.tokensList()
	if len(tokens) != 1 || tokens[0] != "xoxb-r" {
		t.Fatalf("bot tokens = %v", tokens)
	}
	if fx.adapter.runtimeCount() != 1 {
		t.Fatalf("runtime count = %d", fx.adapter.runtimeCount())
	}

	fx.adapter.Stop()
	if all[1].closeCalls() != 1 {
		t.Fatalf("runtime source close calls after Stop = %d", all[1].closeCalls())
	}
	if fx.adapter.runtimeCount() != 0 {
		t.Fatalf("runtimes survived Stop")
	}
}

func TestAdapterValidationNotAcquirableSkipsThenRetries(t *testing.T) {
	log := newEventLog()
	fx := newTestAdapter(t, log, map[string]routeOverride{
		"POST /api/slack-adapter/leases/acquire": func(seq int, body map[string]any) (int, string) {
			if body["kind"] == string(LeaseValidation) && seq == 1 {
				return http.StatusConflict, `{"success":false,"code":"lease_not_acquirable"}`
			}
			return defaultRoute("POST /api/slack-adapter/leases/acquire", seq, body)
		},
	}, nil)
	fx.adapter.Start()

	fx.log.wait(t, "POST /api/slack-adapter/leases/acquire#1")
	if len(fx.sources.all("connection:p:c")) != 0 {
		t.Fatalf("probe created despite unacquirable lease")
	}

	fx.tickers.tick(0) // discovery ticker
	fx.log.wait(t, "POST /api/slack-adapter/leases/hello#1")
	fx.log.wait(t, "POST /api/projects/p/slack-connections/c/deliveries/claim#1")
	if fx.adapter.runtimeCount() != 1 {
		t.Fatalf("runtime count = %d after retry", fx.adapter.runtimeCount())
	}
}

func TestAdapterHelloMismatchStillAcquiresRuntime(t *testing.T) {
	log := newEventLog()
	fx := newTestAdapter(t, log, map[string]routeOverride{
		"POST /api/slack-adapter/leases/hello": func(seq int, body map[string]any) (int, string) {
			return http.StatusConflict, `{"success":false,"code":"app_id_mismatch"}`
		},
	}, nil)
	fx.adapter.Start()

	// Node parity: the hello outcome is reported but never branched on; the
	// Server refuses the runtime lease when the identity did not verify.
	fx.log.wait(t, "POST /api/projects/p/slack-connections/c/deliveries/claim#1")
	if fx.adapter.runtimeCount() != 1 {
		t.Fatalf("runtime count = %d", fx.adapter.runtimeCount())
	}
}

func TestAdapterHeartbeatRenewalUpdatesLeaseAndDrains(t *testing.T) {
	log := newEventLog()
	fx := newTestAdapter(t, log, nil, nil)
	fx.adapter.Start()
	fx.log.wait(t, "POST /api/projects/p/slack-connections/c/deliveries/claim#1")

	fx.tickers.tick(1) // heartbeat ticker
	fx.log.wait(t, "POST /api/slack-adapter/leases/renew#1")
	fx.log.wait(t, "POST /api/projects/p/slack-connections/c/deliveries/claim#2")

	lease, ok := fx.adapter.runtimeLeaseOf("connection:p:c")
	if !ok || lease.Generation != 6 || lease.LeaseID != "r1" {
		t.Fatalf("lease = %#v", lease)
	}
}

func TestAdapterStaleRenewalEvictsAndRediscovers(t *testing.T) {
	log := newEventLog()
	fx := newTestAdapter(t, log, map[string]routeOverride{
		"POST /api/slack-adapter/leases/renew": func(seq int, body map[string]any) (int, string) {
			return 200, `{"success":true,"data":null}`
		},
	}, nil)
	fx.adapter.Start()
	fx.log.wait(t, "POST /api/projects/p/slack-connections/c/deliveries/claim#1")

	evicted := make(chan string, 1)
	runtimeSource := fx.sources.last("connection:p:c")
	runtimeSource.closeSignal = evicted

	fx.tickers.tick(1) // stale renewal evicts
	select {
	case <-evicted:
	case <-time.After(2 * time.Second):
		t.Fatalf("stale runtime was not disconnected")
	}
	if fx.adapter.runtimeCount() != 0 {
		t.Fatalf("evicted runtime stayed in the map")
	}

	fx.tickers.tick(0) // discovery re-adds
	fx.log.wait(t, "POST /api/slack-adapter/leases/hello#2")
	fx.log.wait(t, "POST /api/projects/p/slack-connections/c/deliveries/claim#2")

	lease, ok := fx.adapter.runtimeLeaseOf("connection:p:c")
	if !ok || lease.Generation != 5 {
		t.Fatalf("replacement lease = %#v, want a fresh generation 5", lease)
	}
	if fx.adapter.runtimeCount() != 1 {
		t.Fatalf("runtime count = %d", fx.adapter.runtimeCount())
	}
}

func TestAdapterIngressStaleEvictsOnlyThatTarget(t *testing.T) {
	log := newEventLog()
	fx := newTestAdapter(t, log, map[string]routeOverride{
		"GET /api/slack-adapter/leases/targets": func(seq int, body map[string]any) (int, string) {
			return 200, `{"success":true,"data":[` +
				`{"kind":"connection","projectId":"p","connectionId":"c"},` +
				`{"kind":"connection","projectId":"q","connectionId":"d"}]}`
		},
		"/projects/p/slack-connections/c/ingress": func(seq int, body map[string]any) (int, string) {
			return http.StatusConflict, `{"success":false,"code":"lease_stale_or_expired"}`
		},
	}, nil)
	fx.adapter.Start()
	fx.log.wait(t, "POST /api/projects/q/slack-connections/d/deliveries/claim#1")
	fx.log.wait(t, "POST /api/projects/p/slack-connections/c/deliveries/claim#1")

	pSource := fx.sources.last("connection:p:c")
	qSource := fx.sources.last("connection:q:d")
	pEvicted := make(chan string, 1)
	pSource.closeSignal = pEvicted

	pSource.emit(t, messageEvent(1))

	select {
	case <-pEvicted:
	case <-time.After(2 * time.Second):
		t.Fatalf("stale target was not disconnected")
	}
	if qSource.closeCalls() != 0 {
		t.Fatalf("healthy sibling was disconnected")
	}
	if fx.adapter.runtimeCount() != 1 {
		t.Fatalf("runtime count = %d, want only the healthy sibling", fx.adapter.runtimeCount())
	}
}

func TestAdapterSupersededCallbackIsSwallowed(t *testing.T) {
	log := newEventLog()
	gate := make(chan struct{})
	fx := newTestAdapter(t, log, map[string]routeOverride{
		"POST /api/slack-adapter/leases/renew": func(seq int, body map[string]any) (int, string) {
			return 200, `{"success":true,"data":null}`
		},
		"/projects/p/slack-connections/c/ingress": func(seq int, body map[string]any) (int, string) {
			if body["leaseId"] == "r1" {
				<-gate
				return http.StatusConflict, `{"success":false,"code":"lease_stale_or_expired"}`
			}
			return 200, `{"success":true,"data":{"kind":"accepted"}}`
		},
	}, nil)
	fx.adapter.Start()
	fx.log.wait(t, "POST /api/projects/p/slack-connections/c/deliveries/claim#1")

	evicted := make(chan string, 1)
	oldSource := fx.sources.last("connection:p:c")
	oldSource.closeSignal = evicted

	oldSource.emit(t, messageEvent(1))
	fx.log.wait(t, "/ingress#1") // recorded before the gate blocks

	fx.tickers.tick(1) // stale renewal evicts while the callback is in flight
	select {
	case <-evicted:
	case <-time.After(2 * time.Second):
		t.Fatalf("old runtime was not evicted")
	}

	fx.tickers.tick(0) // discovery installs the replacement
	fx.log.wait(t, "POST /api/slack-adapter/leases/hello#2")
	fx.log.wait(t, "/deliveries/claim#2")

	// Releasing the gate lets the superseded callback finish against a stale
	// snapshot; its stale-lease response must not touch the replacement.
	close(gate)
	fx.tickers.tick(4) // the replacement's delivery poll proves it is alive
	fx.log.wait(t, "/deliveries/claim#3")

	replacement := fx.sources.last("connection:p:c")
	if replacement.closeCalls() != 0 {
		t.Fatalf("the replacement runtime was torn down by a superseded callback")
	}
	if fx.adapter.runtimeCount() != 1 {
		t.Fatalf("runtime count = %d", fx.adapter.runtimeCount())
	}
}

func TestAdapterBackpressureNoticeBeforeAck(t *testing.T) {
	log := newEventLog()
	fx := newTestAdapter(t, log, map[string]routeOverride{
		"/projects/p/slack-connections/c/ingress": func(seq int, body map[string]any) (int, string) {
			return 200, `{"success":true,"data":{"kind":"backpressured","reason":"slow down"}}`
		},
	}, func(options *AdapterOptions) {
		previous := options.NewSlackClient
		options.NewSlackClient = func(botToken string) (SlackClient, error) {
			client, err := previous(botToken)
			if err != nil {
				return nil, err
			}
			wrapped := client.(*fakeSlack)
			wrapped.postFn = func(call postCall) (*PostedMessage, error) {
				log.add("notice:" + call.text)
				return &PostedMessage{Ts: "9.9"}, nil
			}
			return wrapped, nil
		}
	})
	fx.adapter.Start()
	fx.log.wait(t, "POST /api/projects/p/slack-connections/c/deliveries/claim#1")

	fx.sources.last("connection:p:c").emit(t, messageEvent(1))
	// The event acknowledgement is a client-side callback; the observable
	// boundary after it is the drain's next claim on the same route.
	fx.log.wait(t, "POST /api/projects/p/slack-connections/c/deliveries/claim#2")

	noticeIndex := log.indexOf("notice:slow down")
	drainIndex := log.indexOf("POST /api/projects/p/slack-connections/c/deliveries/claim#2")
	if noticeIndex < 0 || drainIndex < 0 {
		t.Fatalf("missing events: notice=%d drain=%d", noticeIndex, drainIndex)
	}
	if noticeIndex > drainIndex {
		t.Fatalf("backpressure notice must precede the acknowledgement and drain")
	}
}

func TestAdapterInteractionAcksBeforeForwarding(t *testing.T) {
	log := newEventLog()
	fx := newTestAdapter(t, log, nil, nil)
	fx.adapter.Start()
	fx.log.wait(t, "POST /api/projects/p/slack-connections/c/deliveries/claim#1")

	interaction := InboundEvent{
		Interaction: &InteractionEnvelope{
			EventType:        "block_actions",
			APIAppID:         "A1",
			InteractionID:    "i1",
			TeamID:           "T123",
			ConversationID:   "C1",
			MessageTs:        "1.1",
			ActorSlackUserID: "U1",
			ActionID:         "act",
			ActionValue:      "v",
		},
		Ack: func(context.Context) error {
			log.add("interaction-acked")
			return nil
		},
	}
	fx.sources.last("connection:p:c").emit(t, interaction)
	fx.log.wait(t, "/interactions#1")

	ackIndex := log.indexOf("interaction-acked")
	forwardIndex := log.indexOf("/interactions#1")
	if ackIndex < 0 || forwardIndex < 0 {
		t.Fatalf("missing events: ack=%d forward=%d", ackIndex, forwardIndex)
	}
	if ackIndex > forwardIndex {
		t.Fatalf("interactions must be acknowledged before forwarding")
	}
}

func TestAdapterMaxInFlightSerializesForwarding(t *testing.T) {
	log := newEventLog()
	gate := make(chan struct{})
	var entered sync.WaitGroup
	entered.Add(1)
	var mu sync.Mutex
	enteredCount := 0
	fx := newTestAdapter(t, log, map[string]routeOverride{
		"/projects/p/slack-connections/c/ingress": func(seq int, body map[string]any) (int, string) {
			mu.Lock()
			enteredCount++
			count := enteredCount
			mu.Unlock()
			if count == 1 {
				entered.Done()
				<-gate
			}
			return 200, `{"success":true,"data":{"kind":"accepted"}}`
		},
	}, func(options *AdapterOptions) {
		options.MaxInFlight = 1
	})
	fx.adapter.Start()
	fx.log.wait(t, "POST /api/projects/p/slack-connections/c/deliveries/claim#1")

	source := fx.sources.last("connection:p:c")
	source.emit(t, messageEvent(1))
	source.emit(t, messageEvent(2))

	entered.Wait()
	fx.log.wait(t, "/ingress#1")
	mu.Lock()
	count := enteredCount
	mu.Unlock()
	if count != 1 {
		t.Fatalf("second event entered ingress while the first held the slot")
	}

	close(gate)
	fx.log.wait(t, "/ingress#2")
	fx.log.wait(t, "/projects/p/slack-connections/c/deliveries/claim#3")
}

func TestAdapterDrainCoalescesConcurrentTriggers(t *testing.T) {
	log := newEventLog()
	gate := make(chan struct{})
	fx := newTestAdapter(t, log, map[string]routeOverride{
		"/deliveries/claim": func(seq int, body map[string]any) (int, string) {
			if seq == 2 {
				<-gate
			}
			return 200, `{"success":true,"data":null}`
		},
	}, nil)
	fx.adapter.Start()
	fx.log.wait(t, "/deliveries/claim#1") // initial drain finished

	fx.tickers.tick(2) // delivery poll enters a gated claim
	fx.log.wait(t, "/deliveries/claim#2")
	fx.tickers.tick(2) // second trigger must coalesce, not run concurrently

	close(gate)
	fx.log.wait(t, "/deliveries/claim#3") // coalesced pass runs after the first
}

func TestAdapterRequestedDrainSurvivesGenerationBump(t *testing.T) {
	log := newEventLog()
	gate := make(chan struct{})
	fx := newTestAdapter(t, log, map[string]routeOverride{
		"/deliveries/claim": func(seq int, body map[string]any) (int, string) {
			if seq == 2 {
				<-gate
			}
			return 200, `{"success":true,"data":null}`
		},
	}, nil)
	fx.adapter.Start()
	fx.log.wait(t, "/deliveries/claim#1")

	fx.tickers.tick(2) // gated delivery drain holds the runtime's draining flag
	fx.log.wait(t, "/deliveries/claim#2")
	// A renewal lands while that pass is open: it bumps the generation and
	// requests a follow-up drain from inside refresh.
	fx.tickers.tick(1)
	fx.log.wait(t, "POST /api/slack-adapter/leases/renew#1")

	close(gate)
	// The requested pass must survive the generation bump.
	fx.log.wait(t, "/deliveries/claim#3")
}

func TestAdapterStopClosesEveryRuntime(t *testing.T) {
	log := newEventLog()
	fx := newTestAdapter(t, log, map[string]routeOverride{
		"GET /api/slack-adapter/leases/targets": func(seq int, body map[string]any) (int, string) {
			return 200, `{"success":true,"data":[` +
				`{"kind":"connection","projectId":"p","connectionId":"c"},` +
				`{"kind":"connection","projectId":"q","connectionId":"d"}]}`
		},
	}, nil)
	fx.adapter.Start()
	fx.log.wait(t, "POST /api/projects/q/slack-connections/d/deliveries/claim#1")
	fx.log.wait(t, "POST /api/projects/p/slack-connections/c/deliveries/claim#1")

	fx.adapter.Stop()
	fx.adapter.Stop() // idempotent

	for key, sources := range map[string][]*fakeSource{
		"connection:p:c": fx.sources.all("connection:p:c"),
		"connection:q:d": fx.sources.all("connection:q:d"),
	} {
		runtimeSource := sources[len(sources)-1]
		if runtimeSource.closeCalls() != 1 {
			t.Fatalf("%s runtime close calls = %d, want 1", key, runtimeSource.closeCalls())
		}
	}
	if fx.adapter.runtimeCount() != 0 {
		t.Fatalf("runtimes survived Stop")
	}
}
