package mohistslack

import (
	"context"
	"sync"
	"time"
)

// manualTicker never fires on its own; tests deliver ticks by sending on Ch.
type manualTicker struct {
	Ch chan time.Time
}

func (t *manualTicker) C() <-chan time.Time { return t.Ch }
func (t *manualTicker) Stop()               {}

// orderedLog records cross-component call order for assertions.
type orderedLog struct {
	mu      sync.Mutex
	entries []string
}

func (l *orderedLog) add(entry string) {
	l.mu.Lock()
	l.entries = append(l.entries, entry)
	l.mu.Unlock()
}

func (l *orderedLog) snapshot() []string {
	l.mu.Lock()
	defer l.mu.Unlock()
	return append([]string(nil), l.entries...)
}

// fakeSocket is a controllable SocketClient for state-machine tests.
type fakeSocket struct {
	mu           sync.Mutex
	appID        string
	startErr     error
	starts       int
	disconnected bool
	handler      func(SocketEvent)
	order        *orderedLog
}

func newFakeSocket(appID string) *fakeSocket { return &fakeSocket{appID: appID} }

func (s *fakeSocket) Start(context.Context) (string, error) {
	s.mu.Lock()
	s.starts++
	startErr, appID := s.startErr, s.appID
	s.mu.Unlock()
	if startErr != nil {
		return "", startErr
	}
	return appID, nil
}

func (s *fakeSocket) OnEvent(handler func(SocketEvent)) {
	s.mu.Lock()
	s.handler = handler
	s.mu.Unlock()
}

func (s *fakeSocket) Disconnect(context.Context) error {
	s.mu.Lock()
	s.disconnected = true
	s.mu.Unlock()
	return nil
}

// emit runs the registered handler synchronously and reports whether the
// flow acknowledged the event.
func (s *fakeSocket) emit(body any) bool {
	acked := false
	ack := func() {
		acked = true
		if s.order != nil {
			s.order.add("ack")
		}
	}
	s.mu.Lock()
	handler := s.handler
	s.mu.Unlock()
	if handler != nil {
		handler(SocketEvent{Body: body, Ack: ack})
	}
	return acked
}

func (s *fakeSocket) wasDisconnected() bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.disconnected
}

func (s *fakeSocket) startCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.starts
}

// fakeWeb records WebClient calls and replays scripted outcomes.
type fakeWeb struct {
	mu            sync.Mutex
	order         *orderedLog
	posts         []PostMessageInput
	updates       []UpdateMessageInput
	adds          [][3]string
	removes       [][3]string
	gets          [][2]string
	historyInputs []HistoryInput
	uploads       []FileUploadInput

	postTS       string
	postErr      error
	updateTS     string
	updateErr    error
	reactionErr  error
	historyFn    func(HistoryInput) ([]HistoryMessage, error)
	reactionList []string
	getErr       error
	uploadResult FileUploadResult
	uploadErr    error
}

func (w *fakeWeb) PostMessage(_ context.Context, input PostMessageInput) (string, error) {
	w.mu.Lock()
	w.posts = append(w.posts, input)
	if w.order != nil {
		w.order.add("post")
	}
	postTS, postErr := w.postTS, w.postErr
	w.mu.Unlock()
	return postTS, postErr
}

func (w *fakeWeb) UpdateMessage(_ context.Context, input UpdateMessageInput) (string, error) {
	w.mu.Lock()
	w.updates = append(w.updates, input)
	if w.order != nil {
		w.order.add("update")
	}
	updateTS, updateErr := w.updateTS, w.updateErr
	w.mu.Unlock()
	return updateTS, updateErr
}

func (w *fakeWeb) AddReaction(_ context.Context, channel, name, timestamp string) error {
	w.mu.Lock()
	w.adds = append(w.adds, [3]string{channel, name, timestamp})
	err := w.reactionErr
	w.mu.Unlock()
	return err
}

func (w *fakeWeb) RemoveReaction(_ context.Context, channel, name, timestamp string) error {
	w.mu.Lock()
	w.removes = append(w.removes, [3]string{channel, name, timestamp})
	err := w.reactionErr
	w.mu.Unlock()
	return err
}

func (w *fakeWeb) GetReactions(_ context.Context, channel, timestamp string) ([]string, error) {
	w.mu.Lock()
	w.gets = append(w.gets, [2]string{channel, timestamp})
	names, getErr := w.reactionList, w.getErr
	w.mu.Unlock()
	return names, getErr
}

func (w *fakeWeb) GetConversationHistory(_ context.Context, input HistoryInput) ([]HistoryMessage, error) {
	w.mu.Lock()
	w.historyInputs = append(w.historyInputs, input)
	fn := w.historyFn
	w.mu.Unlock()
	if fn != nil {
		return fn(input)
	}
	return nil, nil
}

func (w *fakeWeb) UploadFileV2(_ context.Context, input FileUploadInput) (FileUploadResult, error) {
	w.mu.Lock()
	w.uploads = append(w.uploads, input)
	result, uploadErr := w.uploadResult, w.uploadErr
	w.mu.Unlock()
	return result, uploadErr
}

func (w *fakeWeb) postCount() int {
	w.mu.Lock()
	defer w.mu.Unlock()
	return len(w.posts)
}

func (w *fakeWeb) updateCount() int {
	w.mu.Lock()
	defer w.mu.Unlock()
	return len(w.updates)
}

// fakeTransport records Server calls and replays scripted responses.
type fakeTransport struct {
	mu               sync.Mutex
	order            *orderedLog
	targets          []Target
	validationLeases []ValidationLease
	runtimeLeases    []RuntimeLease
	acquireKinds     []LeaseKind
	renewQueue       []*LeaseRenewal
	renewErr         error
	claimCalls       int
	claimEntered     chan struct{}
	claimErr         error
	ingressResults   []IngressResult
	ingressErr       error
	interactionState []string
	claimQueue       []*Delivery
	uncertainQueue   []*Delivery

	ingressGate chan struct{}
	claimGate   chan struct{}
	// ingressHook runs inside Ingress after the envelope is recorded, so
	// tests can land concurrent state changes mid-call deterministically.
	ingressHook func()

	envelopes  []Envelope
	interactns []InteractionEnvelope
	acks       []DeliveryAck
	helloApps  []string
}

func (t *fakeTransport) Discover(context.Context) ([]Target, error) {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.targets, nil
}

func (t *fakeTransport) AcquireLease(_ context.Context, _ Target, kind LeaseKind, _ string) (Lease, error) {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.acquireKinds = append(t.acquireKinds, kind)
	switch kind {
	case LeaseValidation:
		if len(t.validationLeases) == 0 {
			return nil, nil
		}
		lease := t.validationLeases[0]
		t.validationLeases = t.validationLeases[1:]
		return lease, nil
	case LeaseRuntime:
		if len(t.runtimeLeases) == 0 {
			return nil, nil
		}
		lease := t.runtimeLeases[0]
		t.runtimeLeases = t.runtimeLeases[1:]
		return lease, nil
	default:
		return nil, nil
	}
}

func (t *fakeTransport) RenewLease(context.Context, Target, string, string) (*LeaseRenewal, error) {
	t.mu.Lock()
	defer t.mu.Unlock()
	if len(t.renewQueue) == 0 {
		return nil, nil
	}
	renewal := t.renewQueue[0]
	t.renewQueue = t.renewQueue[1:]
	return renewal, nil
}

func (t *fakeTransport) ReportHello(_ context.Context, _ Target, _, appID string) (HelloOutcome, error) {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.helloApps = append(t.helloApps, appID)
	return HelloVerified, nil
}

func (t *fakeTransport) Ingress(ctx context.Context, _ Target, envelope Envelope, _, _ string) (IngressResult, error) {
	t.mu.Lock()
	gate := t.ingressGate
	t.mu.Unlock()
	if gate != nil {
		select {
		case <-gate:
		case <-ctx.Done():
			return IngressResult{}, ctx.Err()
		}
	}
	t.mu.Lock()
	hook := t.ingressHook
	if hook != nil {
		t.mu.Unlock()
		hook()
		t.mu.Lock()
	}
	defer t.mu.Unlock()
	t.envelopes = append(t.envelopes, envelope)
	if t.order != nil {
		t.order.add("ingress")
	}
	if len(t.ingressResults) == 0 {
		// Mirror the production transport: a result without an explicit
		// owner is server-owned silence.
		return IngressResult{Kind: "accepted", ResponseOwner: ResponseOwnerNone}, nil
	}
	result := t.ingressResults[0]
	t.ingressResults = t.ingressResults[1:]
	return result, t.ingressErr
}

func (t *fakeTransport) Interaction(_ context.Context, _ Target, envelope InteractionEnvelope, _, _ string) (InteractionResult, error) {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.interactns = append(t.interactns, envelope)
	if t.order != nil {
		t.order.add("interaction")
	}
	if len(t.interactionState) == 0 {
		return InteractionResult{State: "accepted"}, nil
	}
	state := t.interactionState[0]
	t.interactionState = t.interactionState[1:]
	return InteractionResult{State: state}, nil
}

func (t *fakeTransport) ClaimDelivery(ctx context.Context, _ Target, _, _ string) (*Delivery, error) {
	t.mu.Lock()
	entered := t.claimEntered
	t.mu.Unlock()
	if entered != nil {
		select {
		case entered <- struct{}{}:
		default:
		}
	}
	t.mu.Lock()
	gate := t.claimGate
	t.mu.Unlock()
	if gate != nil {
		select {
		case <-gate:
		case <-ctx.Done():
			return nil, ctx.Err()
		}
	}
	t.mu.Lock()
	t.claimCalls++
	defer t.mu.Unlock()
	if t.claimErr != nil {
		return nil, t.claimErr
	}
	if len(t.claimQueue) == 0 {
		return nil, nil
	}
	delivery := t.claimQueue[0]
	t.claimQueue = t.claimQueue[1:]
	return delivery, nil
}

func (t *fakeTransport) ClaimUncertainDelivery(_ context.Context, _ Target, _, _ string) (*Delivery, error) {
	t.mu.Lock()
	defer t.mu.Unlock()
	if len(t.uncertainQueue) == 0 {
		return nil, nil
	}
	delivery := t.uncertainQueue[0]
	t.uncertainQueue = t.uncertainQueue[1:]
	return delivery, nil
}

func (t *fakeTransport) AckDelivery(_ context.Context, _ Target, ack DeliveryAck, _ string) error {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.acks = append(t.acks, ack)
	if t.order != nil {
		t.order.add("ack:" + ack.Outcome + ":" + ack.ID)
	}
	return nil
}

func (t *fakeTransport) ackCount() int {
	t.mu.Lock()
	defer t.mu.Unlock()
	return len(t.acks)
}

func (t *fakeTransport) ingressCount() int {
	t.mu.Lock()
	defer t.mu.Unlock()
	return len(t.envelopes)
}

// testHarness wires an Adapter against fakes with manual tickers so tests
// drive every flow synchronously.
type testHarness struct {
	adapter   *Adapter
	sockets   []*fakeSocket
	webs      []*fakeWeb
	transport *fakeTransport
	order     *orderedLog

	// presets applied to each newly created web client.
	pendingPostTS   string
	pendingUpdateTS string
}

func newTestHarness(connections ...Target) *testHarness {
	order := &orderedLog{}
	transport := &fakeTransport{targets: connections, order: order}
	harness := &testHarness{
		transport: transport,
		order:     order,
	}
	harness.adapter = NewAdapter(AdapterOptions{
		AdapterID:     "adapter-test",
		Transport:     transport,
		SocketFactory: harness.newSocket,
		WebFactory:    harness.newWeb,
		TickerFactory: func(time.Duration) Ticker { return &manualTicker{Ch: make(chan time.Time)} },
	})
	return harness
}

func (h *testHarness) newSocket(string, Target) SocketClient {
	socket := newFakeSocket("A1")
	socket.order = h.order
	h.sockets = append(h.sockets, socket)
	return socket
}

func (h *testHarness) newWeb(string, Target) WebClient {
	web := &fakeWeb{
		order:    h.order,
		postTS:   h.pendingPostTS,
		updateTS: h.pendingUpdateTS,
	}
	h.webs = append(h.webs, web)
	return web
}

func (h *testHarness) connect(target Target) (*runtime, error) {
	return h.connectWithRuntime(target,
		RuntimeLease{LeaseID: "lease-1", AppToken: "xapp-t", BotToken: "xoxb-t", ExpiresAt: "soon"},
	)
}

func (h *testHarness) connectWithRuntime(target Target, runtime RuntimeLease) (*runtime, error) {
	return h.connectWithLeases(target, ValidationLease{}, runtime)
}

func (h *testHarness) connectWithLeases(target Target, validation ValidationLease, runtime RuntimeLease) (*runtime, error) {
	h.adapter.mu.Lock()
	existing := h.adapter.runtimes[target.Key()]
	h.adapter.mu.Unlock()
	if existing != nil {
		return existing, nil
	}
	h.transport.mu.Lock()
	if validation != (ValidationLease{}) {
		h.transport.validationLeases = append(h.transport.validationLeases, validation)
	}
	if runtime != (RuntimeLease{}) {
		h.transport.runtimeLeases = append(h.transport.runtimeLeases, runtime)
	}
	h.transport.mu.Unlock()
	// connectTarget runs the probe, lease acquisition, socket open, and the
	// initial drain synchronously, so tests observe settled state after it.
	if err := h.adapter.connectTarget(context.Background(), target); err != nil {
		return nil, err
	}
	h.adapter.mu.Lock()
	rt := h.adapter.runtimes[target.Key()]
	h.adapter.mu.Unlock()
	return rt, nil
}

func connectionTarget(project, connection string) ConnectionTarget {
	return ConnectionTarget{ProjectID: project, ConnectionID: connection}
}

func messageBody(channel, ts, text string) map[string]any {
	return map[string]any{
		"type":       "message",
		"api_app_id": "A1",
		"team_id":    "T1",
		"channel":    channel,
		"ts":         ts,
		"user":       "U1",
		"text":       text,
	}
}
