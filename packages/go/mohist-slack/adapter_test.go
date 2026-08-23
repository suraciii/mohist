package mohistslack

import (
	"context"
	"errors"
	"testing"
	"testing/synctest"
)

func TestValidationProbeReportsHelloOnceAndCreatesNoRuntime(t *testing.T) {
	h := newTestHarness()
	target := connectionTarget("p1", "c1")
	if _, err := h.connectWithLeases(target,
		ValidationLease{LeaseID: "v-1", AppToken: "xapp-v", ExpectedAppID: "A1", ExpiresAt: "soon"},
		RuntimeLease{},
	); err != nil {
		t.Fatalf("connectTarget() error = %v", err)
	}
	if got := len(h.transport.acquireKinds); got != 2 ||
		h.transport.acquireKinds[0] != LeaseValidation || h.transport.acquireKinds[1] != LeaseRuntime {
		t.Fatalf("acquire kinds = %v, want [validation runtime]", h.transport.acquireKinds)
	}
	if got := len(h.transport.helloApps); got != 1 || h.transport.helloApps[0] != "A1" {
		t.Fatalf("hello reports = %v, want [A1]", h.transport.helloApps)
	}
	if !h.sockets[0].wasDisconnected() {
		t.Fatal("probe socket was not disconnected")
	}
	h.adapter.mu.Lock()
	runtimes := len(h.adapter.runtimes)
	h.adapter.mu.Unlock()
	if runtimes != 0 {
		t.Fatalf("validation probe created %d runtime(s), want 0", runtimes)
	}
}

func TestDiscoveryWithoutRuntimeLeaseStartsNothing(t *testing.T) {
	h := newTestHarness()
	target := connectionTarget("p1", "c1")
	if err := h.adapter.connectTarget(context.Background(), target); err != nil {
		t.Fatalf("connectTarget() error = %v", err)
	}
	for _, socket := range h.sockets {
		if socket.startCount() != 0 {
			t.Fatal("a socket was started without a lease")
		}
	}
	if h.transport.ingressCount() != 0 {
		t.Fatal("deliveries were claimed without a runtime")
	}
}

func TestConnectOpensRuntimeAndInitialDrainSettlesDelivery(t *testing.T) {
	h := newTestHarness()
	target := connectionTarget("p1", "c1")
	h.transport.claimQueue = []*Delivery{{
		ID:             "d-1",
		ConversationID: "D1",
		PayloadJSON:    `{"text":"accepted"}`,
	}}
	h.pendingPostTS = "1700.5"
	rt, err := h.connect(target)
	if err != nil {
		t.Fatalf("connect() error = %v", err)
	}
	if rt == nil {
		t.Fatal("runtime missing after connect")
	}
	if len(h.sockets) != 1 { // runtime socket only; no validation lease means no probe
		t.Fatalf("sockets created = %d, want 1", len(h.sockets))
	}
	if got := h.webs[0].postCount(); got != 1 {
		t.Fatalf("posts = %d, want 1", got)
	}
	if got := h.transport.ackCount(); got != 1 {
		t.Fatalf("acks = %d, want 1", got)
	}
	ack := h.transport.acks[0]
	if ack.Outcome != OutcomeDelivered || ack.ID != "d-1" {
		t.Fatalf("ack = %+v, want delivered d-1", ack)
	}
	if ack.ProviderMessageIdentity == nil || ack.ProviderMessageIdentity.MessageTs == "" {
		t.Fatalf("ack identity missing: %+v", ack)
	}
}

func TestStaleRenewalEvictsRuntimeAndDropsLateEvents(t *testing.T) {
	target := connectionTarget("p1", "c1")
	h := newTestHarness()
	rt, err := h.connect(target)
	if err != nil {
		t.Fatalf("connect() error = %v", err)
	}
	socket := h.sockets[0]

	h.adapter.refresh(rt) // no renewal queued → stale

	h.adapter.mu.Lock()
	_, alive := h.adapter.runtimes[target.Key()]
	h.adapter.mu.Unlock()
	if alive {
		t.Fatal("runtime survived a stale renewal")
	}
	if !socket.wasDisconnected() {
		t.Fatal("evicted runtime kept its socket open")
	}

	// A late event from the evicted socket must not reach the Server.
	if acked := socket.emit(messageBody("D1", "1700.1", "late")); acked {
		t.Fatal("late event was acknowledged after eviction")
	}
	if h.transport.ingressCount() != 0 {
		t.Fatal("late event reached the Server after eviction")
	}
}

func TestExtendedRenewalKeepsRuntime(t *testing.T) {
	target := connectionTarget("p1", "c1")
	h := newTestHarness()
	rt, err := h.connect(target)
	if err != nil {
		t.Fatalf("connect() error = %v", err)
	}
	h.transport.renewQueue = []*LeaseRenewal{
		{LeaseID: "lease-1", Kind: LeaseRuntime, Generation: 7, ExpiresAt: "later"},
	}

	h.adapter.refresh(rt)

	h.adapter.mu.Lock()
	current := h.adapter.runtimes[target.Key()]
	h.adapter.mu.Unlock()
	if current != rt {
		t.Fatal("extended renewal replaced the runtime")
	}
	if rt.lease.Generation != 7 || rt.lease.ExpiresAt != "later" {
		t.Fatalf("lease not extended in place: %+v", rt.lease)
	}
	if h.sockets[0].wasDisconnected() {
		t.Fatal("renewed runtime disconnected its socket")
	}
}

func TestSupersededRuntimeFailureNeverEvictsSuccessor(t *testing.T) {
	target := connectionTarget("p1", "c1")
	h := newTestHarness()
	superseded, err := h.connect(target)
	if err != nil {
		t.Fatalf("connect() error = %v", err)
	}

	// A foreign renewal replaces the runtime while the old one's event flow
	// sits inside its ingress call: the hook lands the replacement and turns
	// the pending response into the Server's stale-lease rejection.
	successorSocket := newFakeSocket("A1")
	successor := &runtime{
		key:    superseded.key,
		target: superseded.target,
		lease:  RuntimeLease{LeaseID: "lease-2", AppToken: "xapp-t", BotToken: "xoxb-t", ExpiresAt: "soon"},
		done:   make(chan struct{}),
	}
	successor.socket = successorSocket
	successor.web = &fakeWeb{}
	a := h.adapter
	// Mirror what openRuntimeSocket wires for a real runtime.
	successorSocket.OnEvent(func(event SocketEvent) {
		a.onSocketEvent(successor, successorSocket, event)
	})
	h.transport.mu.Lock()
	h.transport.ingressHook = func() {
		a.mu.Lock()
		a.runtimes[target.Key()] = successor
		a.mu.Unlock()
	}
	h.transport.ingressResults = []IngressResult{{
		Kind:          "rejected",
		ResponseOwner: ResponseOwnerNone,
	}}
	h.transport.ingressErr = &APIError{Status: 409, Code: string(HelloLeaseStale)}
	h.transport.mu.Unlock()

	acked := h.sockets[0].emit(messageBody("D1", "1700.4", "late"))

	if acked {
		t.Fatal("the superseded flow acknowledged its event")
	}
	a.mu.Lock()
	current := a.runtimes[target.Key()]
	a.mu.Unlock()
	if current != successor {
		t.Fatal("a superseded runtime's stale error evicted its replacement")
	}
	if !h.sockets[0].wasDisconnected() {
		h.sockets[0].waitDisconnected()
	}
	if !h.sockets[0].wasDisconnected() {
		t.Fatal("the superseded runtime itself was not disconnected")
	}
	if successorSocket.wasDisconnected() {
		t.Fatal("the successor's socket was disconnected")
	}

	// The successor keeps serving: a live event flows through normally.
	h.transport.mu.Lock()
	h.transport.ingressErr = nil
	h.transport.ingressHook = nil
	h.transport.mu.Unlock()
	acked = successorSocket.emit(messageBody("D1", "1700.5", "fresh"))
	t.Logf("DBG successor acked=%v ingressCount=%d order=%v", acked, h.transport.ingressCount(), h.order.snapshot())
	if !acked {
		t.Fatal("the successor failed to acknowledge a live event")
	}
}

func TestTerminalSocketFailureEvictsRuntime(t *testing.T) {
	target := connectionTarget("p1", "c1")
	h := newTestHarness()
	if _, err := h.connect(target); err != nil {
		t.Fatalf("connect() error = %v", err)
	}
	socket := h.sockets[0]

	socket.emitState("error", errors.New("socket runner failed"))
	socket.waitDisconnected()

	h.adapter.mu.Lock()
	_, alive := h.adapter.runtimes[target.Key()]
	h.adapter.mu.Unlock()
	if alive {
		t.Fatal("runtime survived a terminal socket failure")
	}
}

func TestStopCancelsEventTriggeredDeliveryDrain(t *testing.T) {
	target := connectionTarget("p1", "c1")
	h := newTestHarness()
	if _, err := h.connect(target); err != nil {
		t.Fatalf("connect() error = %v", err)
	}
	claimEntered := make(chan struct{}, 1)
	h.transport.mu.Lock()
	h.transport.claimEntered = claimEntered
	h.transport.claimGate = make(chan struct{})
	h.transport.mu.Unlock()

	acked := h.sockets[0].emitAsync(messageBody("D1", "1700.6", "stop"))
	<-claimEntered
	stopped := make(chan struct{})
	go func() {
		h.adapter.Stop()
		close(stopped)
	}()

	<-stopped
	if !<-acked {
		t.Fatal("message was not acknowledged before its delivery drain")
	}
}

func TestStopConvergesWithHandlerTriggeredStaleEviction(t *testing.T) {
	target := connectionTarget("p1", "c1")
	h := newTestHarness()
	if _, err := h.connect(target); err != nil {
		t.Fatalf("connect() error = %v", err)
	}
	ingressEntered := make(chan struct{}, 1)
	ingressGate := make(chan struct{})
	h.transport.mu.Lock()
	h.transport.ingressEntered = ingressEntered
	h.transport.ingressGate = ingressGate
	h.transport.ingressIgnoreCancellation = true
	h.transport.ingressResults = []IngressResult{{Kind: "rejected", ResponseOwner: ResponseOwnerNone}}
	h.transport.ingressErr = &APIError{Status: 409, Code: string(HelloLeaseStale)}
	h.transport.mu.Unlock()

	acked := h.sockets[0].emitAsync(messageBody("D1", "1700.7", "stale"))
	<-ingressEntered
	stopped := make(chan struct{})
	go func() {
		h.adapter.Stop()
		close(stopped)
	}()
	h.sockets[0].waitDisconnectStarted()
	close(ingressGate)

	<-stopped
	if <-acked {
		t.Fatal("stale event was acknowledged")
	}
}

func TestConcurrentStopWaitsForInProgressCleanup(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		target := connectionTarget("p1", "c1")
		h := newTestHarness()
		if _, err := h.connect(target); err != nil {
			t.Fatalf("connect() error = %v", err)
		}
		ingressEntered := make(chan struct{}, 1)
		ingressGate := make(chan struct{})
		h.transport.mu.Lock()
		h.transport.ingressEntered = ingressEntered
		h.transport.ingressGate = ingressGate
		h.transport.ingressIgnoreCancellation = true
		h.transport.mu.Unlock()

		acked := h.sockets[0].emitAsync(messageBody("D1", "1700.8", "concurrent stop"))
		<-ingressEntered
		firstStopped := make(chan struct{})
		go func() {
			h.adapter.Stop()
			close(firstStopped)
		}()
		h.sockets[0].waitDisconnectStarted()
		secondStopped := make(chan struct{})
		go func() {
			h.adapter.Stop()
			close(secondStopped)
		}()
		synctest.Wait()
		select {
		case <-secondStopped:
			t.Fatal("concurrent Stop returned before in-progress cleanup completed")
		default:
		}

		close(ingressGate)
		synctest.Wait()
		<-firstStopped
		<-secondStopped
		if <-acked {
			t.Fatal("message was acknowledged after shutdown removed its runtime")
		}
	})
}

func TestStopWaitsForStartDiscoveryToFinish(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		h := newTestHarness()
		discoverEntered := make(chan struct{}, 1)
		discoverGate := make(chan struct{})
		h.transport.mu.Lock()
		h.transport.discoverEntered = discoverEntered
		h.transport.discoverGate = discoverGate
		h.transport.discoverIgnoreCancellation = true
		h.transport.mu.Unlock()

		startResult := make(chan error, 1)
		go func() { startResult <- h.adapter.Start(context.Background()) }()
		<-discoverEntered
		stopped := make(chan struct{})
		go func() {
			h.adapter.Stop()
			close(stopped)
		}()
		synctest.Wait()
		select {
		case <-stopped:
			t.Fatal("Stop returned before Start discovery completed")
		default:
		}

		close(discoverGate)
		synctest.Wait()
		if err := <-startResult; err != nil {
			t.Fatalf("Start error = %v", err)
		}
		<-stopped
	})
}

func TestAdapterRejectsSecondStart(t *testing.T) {
	h := newTestHarness()
	if err := h.adapter.Start(context.Background()); err != nil {
		t.Fatalf("first Start error = %v", err)
	}
	if err := h.adapter.Start(context.Background()); err == nil {
		t.Fatal("second Start succeeded")
	}
	h.adapter.Stop()
}

func TestStoppedAdapterRejectsNewTrackedWork(t *testing.T) {
	h := newTestHarness()
	h.adapter.Stop()
	if h.adapter.beginWork() {
		h.adapter.wg.Done()
		t.Fatal("stopped adapter accepted new tracked work")
	}
}

func TestTerminalSocketStateAfterStopDoesNotScheduleAnotherDisconnect(t *testing.T) {
	target := connectionTarget("p1", "c1")
	h := newTestHarness()
	if _, err := h.connect(target); err != nil {
		t.Fatalf("connect() error = %v", err)
	}
	socket := h.sockets[0]

	h.adapter.Stop()
	socket.emitState("error", errors.New("late socket failure"))

	if got := socket.disconnectCount(); got != 1 {
		t.Fatalf("disconnect calls = %d, want 1", got)
	}
}

func TestDrainSingleFlightCoalescesTriggers(t *testing.T) {
	target := connectionTarget("p1", "c1")
	h := newTestHarness()
	rt, err := h.connect(target)
	if err != nil {
		t.Fatalf("connect() error = %v", err)
	}
	gate := make(chan struct{})
	h.transport.mu.Lock()
	h.transport.claimGate = gate
	h.transport.claimQueue = []*Delivery{{
		ID:             "d-1",
		ConversationID: "D1",
		PayloadJSON:    `{"text":"first"}`,
	}}
	h.transport.mu.Unlock()

	entered := make(chan struct{}, 1)
	h.transport.mu.Lock()
	h.transport.claimEntered = entered
	h.transport.mu.Unlock()
	drainingDone := make(chan error, 1)
	go func() { drainingDone <- h.adapter.drain(rt) }()

	// Wait until the first pass is parked inside its claim, then coalesce a
	// second trigger onto it and release the gate.
	<-entered
	if err := h.adapter.drain(rt); err != nil {
		t.Fatalf("coalesced drain returned error: %v", err)
	}
	close(gate)
	if err := <-drainingDone; err != nil {
		t.Fatalf("drain() error = %v", err)
	}

	if got := h.transport.ackCount(); got != 1 {
		t.Fatalf("acks = %d, want exactly one settled delivery", got)
	}
	if ack := h.transport.acks[0]; ack.ID != "d-1" {
		t.Fatalf("ack id = %q, want d-1", ack.ID)
	}
}

func TestBackpressureNoticePostsBeforeAcknowledgement(t *testing.T) {
	target := connectionTarget("p1", "c1")
	h := newTestHarness()
	if _, err := h.connect(target); err != nil {
		t.Fatalf("connect() error = %v", err)
	}
	reason := "manager queue is full"
	h.transport.ingressResults = []IngressResult{{
		Kind:          "backpressured",
		ResponseOwner: ResponseOwnerAdapter,
		Reason:        &reason,
	}}

	acked := h.sockets[0].emit(messageBody("D1", "1700.2", "hello"))

	if !acked {
		t.Fatal("backpressured event was never acknowledged")
	}
	entries := h.order.snapshot()
	postIndex, ingressIndex := -1, -1
	for i, entry := range entries {
		switch entry {
		case "post":
			postIndex = i
		case "ingress":
			ingressIndex = i
		}
	}
	if ingressIndex == -1 || postIndex == -1 || postIndex < ingressIndex {
		t.Fatalf("notice order broken: %v", entries)
	}
	// The ack callback ran after emit returned false only when fencing hit;
	// here it must have fired before emit returned, i.e. after the post.
	if got := h.webs[0].postCount(); got != 1 {
		t.Fatalf("notice posts = %d, want 1", got)
	}
	notice := h.webs[0].posts[0]
	if notice.Channel != "D1" || notice.Text != reason {
		t.Fatalf("notice = %+v, want reason posted to D1", notice)
	}
}

func TestInteractionAcksBeforeForwarding(t *testing.T) {
	target := connectionTarget("p1", "c1")
	h := newTestHarness()
	if _, err := h.connect(target); err != nil {
		t.Fatalf("connect() error = %v", err)
	}
	body := map[string]any{
		"type":       "block_actions",
		"api_app_id": "A1",
		"team":       map[string]any{"id": "T1"},
		"user":       map[string]any{"id": "U9"},
		"container":  map[string]any{"channel_id": "C1", "message_ts": "1700.9"},
		"trigger_id": "trig-1",
		"actions": []any{map[string]any{
			"action_id": "act-1",
			"value":     "yes",
		}},
	}

	if acked := h.sockets[0].emit(body); !acked {
		t.Fatal("interaction was not acknowledged")
	}
	entries := h.order.snapshot()
	if len(entries) < 2 || entries[0] != "ack" || entries[1] != "interaction" {
		t.Fatalf("interaction order = %v, want ack then interaction", entries)
	}
	if got := h.transport.interactns[0].ActionValue; got != "yes" {
		t.Fatalf("forwarded action value = %q", got)
	}
}

func TestMessageForwardsBeforeAcknowledgement(t *testing.T) {
	target := connectionTarget("p1", "c1")
	h := newTestHarness()
	if _, err := h.connect(target); err != nil {
		t.Fatalf("connect() error = %v", err)
	}

	acked := h.sockets[0].emit(messageBody("D1", "1700.3", "hi"))
	t.Logf("acked=%v ingressCount=%d order=%v", acked, h.transport.ingressCount(), h.order.snapshot())
	if !acked {
		t.Fatal("message was never acknowledged")
	}
	entries := h.order.snapshot()
	if len(entries) < 2 || entries[0] != "ingress" || entries[1] != "ack" {
		t.Fatalf("message order = %v, want ingress then ack", entries)
	}
	envelope := h.transport.envelopes[len(h.transport.envelopes)-1]
	if envelope.ConversationID != "D1" || envelope.MessageTs != "1700.3" || envelope.SenderKind != SenderHuman {
		t.Fatalf("forwarded envelope = %+v", envelope)
	}
}

func TestMalformedEventIsNotAcknowledged(t *testing.T) {
	target := connectionTarget("p1", "c1")
	h := newTestHarness()
	if _, err := h.connect(target); err != nil {
		t.Fatalf("connect() error = %v", err)
	}
	if acked := h.sockets[0].emit(map[string]any{"type": "message"}); acked {
		t.Fatal("an event without stable identity was acknowledged")
	}
	if h.transport.ingressCount() != 0 {
		t.Fatal("a malformed event reached the Server")
	}
}

func TestStopDisconnectsAllRuntimes(t *testing.T) {
	target := connectionTarget("p1", "c1")
	h := newTestHarness()
	if _, err := h.connect(target); err != nil {
		t.Fatalf("connect() error = %v", err)
	}
	h.adapter.Stop()
	if !h.sockets[0].wasDisconnected() {
		t.Fatal("Stop left the runtime socket connected")
	}
	h.adapter.mu.Lock()
	count := len(h.adapter.runtimes)
	h.adapter.mu.Unlock()
	if count != 0 {
		t.Fatalf("Stop left %d runtime(s)", count)
	}
}
