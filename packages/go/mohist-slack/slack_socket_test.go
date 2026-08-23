package mohistslack

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"net/url"
	"slices"
	"sync"
	"testing"
	"testing/synctest"

	"github.com/slack-go/slack/socketmode"
)

func TestSocketEventPumpReportsHelloAppID(t *testing.T) {
	events := make(chan socketmode.Event, 1)
	events <- socketmode.Event{
		Type: socketmode.EventTypeHello,
		Request: &socketmode.Request{
			ConnectionInfo: socketmode.ConnectionInfo{AppID: "A1"},
		},
	}
	close(events)
	hello := make(chan string, 1)
	failed := make(chan error, 1)

	runSocketEventPump(
		context.Background(), events, discardSocketAck, nil, hello, failed, make(chan struct{}), func(SocketEvent) bool { return true },
	)

	if appID := <-hello; appID != "A1" {
		t.Fatalf("hello app id = %q, want A1", appID)
	}
}

func TestSlackSocketProxyCoversConnectionBootstrap(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	request := make(chan *http.Request, 1)
	proxy := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		request <- r.Clone(r.Context())
		cancel()
		http.Error(w, "cancelled", http.StatusBadGateway)
	}))
	defer proxy.Close()
	proxyURL, err := url.Parse(proxy.URL)
	if err != nil {
		t.Fatal(err)
	}
	proxyURL.User = url.UserPassword("proxy-user", "")
	socket := NewSlackSocket("xapp-test")
	socket.apiURL = "http://slack.invalid/api/"
	socket.SetProxy(proxyURL)

	_, err = socket.Start(ctx)
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("Start error = %v, want context cancellation", err)
	}
	bootstrap := <-request
	if bootstrap.URL.Host != "slack.invalid" || bootstrap.URL.Path != "/api/apps.connections.open" {
		t.Fatalf("bootstrap URL = %s", bootstrap.URL.String())
	}
	if authorization := bootstrap.Header.Get("Authorization"); authorization != "Bearer xapp-test" {
		t.Fatalf("bootstrap authorization = %q", authorization)
	}
	if proxyAuthorization := bootstrap.Header.Get("Proxy-Authorization"); proxyAuthorization != "Basic cHJveHktdXNlcjo=" {
		t.Fatalf("bootstrap proxy authorization = %q", proxyAuthorization)
	}
}

func TestSlackSocketProxyCoversWebSocketConnect(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	connectHost := make(chan string, 1)
	connectAuthorization := make(chan string, 1)
	proxy := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodConnect {
			connectHost <- r.Host
			connectAuthorization <- r.Header.Get("Proxy-Authorization")
			cancel()
			http.Error(w, "cancelled", http.StatusBadGateway)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"ok":true,"url":"wss://socket.slack.invalid/link"}`))
	}))
	defer proxy.Close()
	proxyURL, err := url.Parse(proxy.URL)
	if err != nil {
		t.Fatal(err)
	}
	proxyURL.User = url.UserPassword("proxy-user", "")
	socket := NewSlackSocket("xapp-test")
	socket.apiURL = "http://slack.invalid/api/"
	socket.SetProxy(proxyURL)

	_, err = socket.Start(ctx)
	if err == nil {
		t.Fatal("Start succeeded after the proxy cancelled the WebSocket tunnel")
	}
	if host := <-connectHost; host != "socket.slack.invalid:443" {
		t.Fatalf("WebSocket CONNECT host = %q", host)
	}
	if proxyAuthorization := <-connectAuthorization; proxyAuthorization != "Basic cHJveHktdXNlcjo=" {
		t.Fatalf("WebSocket proxy authorization = %q", proxyAuthorization)
	}
}

func TestSlackSocketConcurrentStartAndDisconnectAlwaysTerminates(t *testing.T) {
	for range 100 {
		socket := NewSlackSocket("xapp-test")
		socket.apiURL = "http://slack.invalid/api/"
		socket.httpClient = &http.Client{Transport: contextBlockingTransport{}}
		start := make(chan struct{})
		startResult := make(chan error, 1)
		disconnectResult := make(chan error, 1)
		go func() {
			<-start
			_, err := socket.Start(context.Background())
			startResult <- err
		}()
		go func() {
			<-start
			disconnectResult <- socket.Disconnect(context.Background())
		}()

		close(start)
		if err := <-disconnectResult; err != nil {
			t.Fatalf("Disconnect error = %v", err)
		}
		if err := <-startResult; err == nil {
			t.Fatal("Start succeeded despite concurrent disconnect")
		}
	}
}

func TestSlackSocketDisconnectClosesIdleProxyConnections(t *testing.T) {
	transport := &idleTrackingTransport{closed: make(chan struct{})}
	socket := NewSlackSocket("xapp-test")
	socket.httpClient = &http.Client{Transport: transport}

	if err := socket.Disconnect(context.Background()); err != nil {
		t.Fatalf("Disconnect error = %v", err)
	}
	<-transport.closed
}

func TestSlackSocketDisconnectWaitsForSocketRunner(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		transport := &cancellationBlockingTransport{
			entered:   make(chan struct{}),
			cancelled: make(chan struct{}),
			release:   make(chan struct{}),
		}
		socket := NewSlackSocket("xapp-test")
		socket.apiURL = "http://slack.invalid/api/"
		socket.httpClient = &http.Client{Transport: transport}
		startResult := make(chan error, 1)
		go func() {
			_, err := socket.Start(context.Background())
			startResult <- err
		}()
		<-transport.entered
		disconnectResult := make(chan error, 1)
		go func() { disconnectResult <- socket.Disconnect(context.Background()) }()
		<-transport.cancelled
		synctest.Wait()
		select {
		case err := <-disconnectResult:
			t.Fatalf("Disconnect returned before the socket runner stopped: %v", err)
		default:
		}
		close(transport.release)
		synctest.Wait()
		if err := <-disconnectResult; err != nil {
			t.Fatalf("Disconnect error = %v", err)
		}
		if err := <-startResult; err == nil {
			t.Fatal("Start succeeded after disconnect")
		}
	})
}

func TestSocketEventPumpDropsBufferedHelloAfterCancellation(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	events := make(chan socketmode.Event, 1)
	events <- socketmode.Event{
		Type: socketmode.EventTypeHello,
		Request: &socketmode.Request{
			ConnectionInfo: socketmode.ConnectionInfo{AppID: "A-late"},
		},
	}
	hello := make(chan string, 1)

	runSocketEventPump(
		ctx,
		events,
		discardSocketAck,
		nil,
		hello,
		make(chan error, 1),
		make(chan struct{}),
		func(SocketEvent) bool { return true },
	)

	select {
	case appID := <-hello:
		t.Fatalf("cancelled pump reported buffered hello %q", appID)
	default:
	}
}

func TestSocketEventPumpFailsWhenEventsCloseBeforeHello(t *testing.T) {
	events := make(chan socketmode.Event)
	close(events)
	failed := make(chan error, 1)

	runSocketEventPump(
		context.Background(), events, discardSocketAck, nil, make(chan string, 1), failed, make(chan struct{}), func(SocketEvent) bool { return true },
	)

	if err := <-failed; !errors.Is(err, errSocketEventsClosed) {
		t.Fatalf("closed events error = %v", err)
	}
}

func TestSocketEventPumpPreservesAckIdentity(t *testing.T) {
	events := make(chan socketmode.Event, 3)
	events <- socketmode.Event{Type: socketmode.EventTypeHello}
	events <- socketPayloadEvent("env-1", map[string]any{"type": "event_one"})
	events <- socketPayloadEvent("env-2", map[string]any{"type": "event_two"})
	close(events)

	var dispatched []SocketEvent
	var acknowledgements []string
	runSocketEventPump(
		context.Background(),
		events,
		func(_ context.Context, envelopeID string) error {
			acknowledgements = append(acknowledgements, envelopeID)
			return nil
		},
		nil,
		make(chan string, 1),
		make(chan error, 1),
		make(chan struct{}),
		func(event SocketEvent) bool {
			dispatched = append(dispatched, event)
			return true
		},
	)

	if len(dispatched) != 2 {
		t.Fatalf("dispatch count = %d, want 2", len(dispatched))
	}
	dispatched[1].Ack()
	dispatched[0].Ack()
	if got, want := acknowledgements, []string{"env-2", "env-1"}; !slices.Equal(got, want) {
		t.Fatalf("acknowledgements = %v, want %v", got, want)
	}
}

func TestSocketRunnerMonitorReportsFailureAfterHello(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	runner := make(chan error, 1)
	connected := make(chan struct{})
	close(connected)
	failedState := make(chan error, 1)
	done := make(chan struct{})
	go func() {
		monitorSocketRunner(
			ctx,
			cancel,
			runner,
			connected,
			func(state string, err error) {
				if state == "error" {
					failedState <- err
				}
			},
			make(chan error, 1),
		)
		close(done)
	}()

	runnerErr := errors.New("runner failed")
	runner <- runnerErr
	if err := <-failedState; !errors.Is(err, runnerErr) {
		t.Fatalf("runner failure = %v", err)
	}
	<-ctx.Done()
	<-done
}

func TestSocketEventExecutorAdmitsInteractionWhenMessageLaneIsSaturated(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	messageStarted := make(chan struct{}, 1)
	releaseMessages := make(chan struct{})
	interactionAcked := make(chan struct{}, 1)
	executor := newSocketEventExecutor(ctx, 1, func(event SocketEvent) {
		if IsSlackInteraction(event.Body) {
			event.Ack()
			return
		}
		select {
		case messageStarted <- struct{}{}:
		default:
		}
		<-releaseMessages
	})

	if !executor.TryDispatch(SocketEvent{Body: map[string]any{"type": "message"}}) {
		t.Fatal("active message was not admitted")
	}
	<-messageStarted
	if !executor.TryDispatch(SocketEvent{Body: map[string]any{"type": "message"}}) {
		t.Fatal("queued message was not admitted")
	}
	if !executor.TryDispatch(SocketEvent{
		Body: map[string]any{"type": "block_actions"},
		Ack:  func() { interactionAcked <- struct{}{} },
	}) {
		t.Fatal("interaction was not admitted while the message lane was saturated")
	}
	<-interactionAcked
	close(releaseMessages)
	cancel()
	executor.Wait()
}

func TestSocketEventExecutorBoundsConcurrentHandlers(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	started := make(chan struct{}, 2)
	release := make(chan struct{})
	finished := make(chan struct{}, 4)
	var mu sync.Mutex
	active := 0
	maximum := 0
	executor := newSocketEventExecutor(ctx, 2, func(SocketEvent) {
		mu.Lock()
		active++
		maximum = max(maximum, active)
		mu.Unlock()
		select {
		case started <- struct{}{}:
		default:
		}
		<-release
		mu.Lock()
		active--
		mu.Unlock()
		finished <- struct{}{}
	})
	if !executor.TryDispatch(SocketEvent{}) || !executor.TryDispatch(SocketEvent{}) {
		t.Fatal("active handlers were not admitted")
	}
	<-started
	<-started
	if !executor.TryDispatch(SocketEvent{}) || !executor.TryDispatch(SocketEvent{}) {
		t.Fatal("bounded queue slots were not admitted")
	}
	if executor.TryDispatch(SocketEvent{}) {
		t.Fatal("event was admitted beyond the active and queued bounds")
	}
	mu.Lock()
	if maximum != 2 {
		t.Fatalf("maximum active handlers = %d, want 2", maximum)
	}
	mu.Unlock()
	close(release)
	for range 4 {
		<-finished
	}
	cancel()
	executor.Wait()
}

func TestSocketEventExecutorCancellationDropsQueuedHandlers(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	started := make(chan struct{}, 1)
	var mu sync.Mutex
	calls := 0
	executor := newSocketEventExecutor(ctx, 1, func(event SocketEvent) {
		mu.Lock()
		calls++
		mu.Unlock()
		select {
		case started <- struct{}{}:
		default:
		}
		<-event.Context.Done()
	})

	if !executor.TryDispatch(SocketEvent{Context: ctx}) {
		t.Fatal("active handler was not admitted")
	}
	<-started
	if !executor.TryDispatch(SocketEvent{Context: ctx}) {
		t.Fatal("queued handler was not admitted")
	}
	cancel()
	executor.Wait()

	mu.Lock()
	defer mu.Unlock()
	if calls != 1 {
		t.Fatalf("handler calls after cancellation = %d, want 1", calls)
	}
}

func TestSocketEventPumpContinuesToInteractionWhenMessageLaneIsSaturated(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	messageStarted := make(chan struct{}, 1)
	releaseMessages := make(chan struct{})
	executor := newSocketEventExecutor(ctx, 1, func(event SocketEvent) {
		if IsSlackInteraction(event.Body) {
			event.Ack()
			return
		}
		select {
		case messageStarted <- struct{}{}:
		default:
		}
		<-releaseMessages
	})
	if !executor.TryDispatch(SocketEvent{Body: map[string]any{"type": "message"}}) {
		t.Fatal("active message was not admitted")
	}
	<-messageStarted
	if !executor.TryDispatch(SocketEvent{Body: map[string]any{"type": "message"}}) {
		t.Fatal("queued message was not admitted")
	}

	events := make(chan socketmode.Event, 3)
	events <- socketmode.Event{Type: socketmode.EventTypeHello}
	events <- socketPayloadEvent("overflow-message", map[string]any{"type": "message"})
	interaction := socketPayloadEvent("live-interaction", map[string]any{"type": "block_actions"})
	interaction.Type = socketmode.EventTypeInteractive
	events <- interaction
	close(events)
	acknowledged := make(chan string, 1)
	backpressured := make(chan error, 1)
	done := make(chan struct{})
	go func() {
		runSocketEventPump(
			ctx,
			events,
			func(_ context.Context, envelopeID string) error {
				acknowledged <- envelopeID
				return nil
			},
			func(state string, apiErr error) {
				if state == "backpressured" {
					backpressured <- apiErr
				}
			},
			make(chan string, 1),
			make(chan error, 1),
			make(chan struct{}),
			executor.TryDispatch,
		)
		close(done)
	}()

	if err := <-backpressured; !errors.Is(err, errSocketDispatchSaturated) {
		t.Fatalf("backpressure error = %v", err)
	}
	if envelopeID := <-acknowledged; envelopeID != "live-interaction" {
		t.Fatalf("acknowledged envelope = %q, want live-interaction", envelopeID)
	}
	<-done
	close(releaseMessages)
	cancel()
	executor.Wait()
}

func socketPayloadEvent(envelopeID string, body any) socketmode.Event {
	payload, err := json.Marshal(body)
	if err != nil {
		panic(err)
	}
	return socketmode.Event{
		Type: socketmode.EventTypeEventsAPI,
		Request: &socketmode.Request{
			EnvelopeID: envelopeID,
			Payload:    payload,
		},
	}
}

func discardSocketAck(context.Context, string) error { return nil }

type contextBlockingTransport struct{}

func (contextBlockingTransport) RoundTrip(request *http.Request) (*http.Response, error) {
	<-request.Context().Done()
	return nil, request.Context().Err()
}

type idleTrackingTransport struct {
	once   sync.Once
	closed chan struct{}
}

func (*idleTrackingTransport) RoundTrip(*http.Request) (*http.Response, error) {
	return nil, errors.New("unexpected request")
}

func (t *idleTrackingTransport) CloseIdleConnections() {
	t.once.Do(func() { close(t.closed) })
}

type cancellationBlockingTransport struct {
	once      sync.Once
	entered   chan struct{}
	cancelled chan struct{}
	release   chan struct{}
}

func (t *cancellationBlockingTransport) RoundTrip(request *http.Request) (*http.Response, error) {
	t.once.Do(func() { close(t.entered) })
	<-request.Context().Done()
	close(t.cancelled)
	<-t.release
	return nil, request.Context().Err()
}
