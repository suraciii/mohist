package mohistslack

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"

	"github.com/gorilla/websocket"
	"github.com/slack-go/slack"
	"github.com/slack-go/slack/socketmode"
)

const (
	slackAPIBaseURL          = "https://slack.com/api/"
	defaultSocketMaxInFlight = 8

	// defaultConnectTimeout bounds how long Start waits for the Socket Mode
	// hello; a dead endpoint fails the target's connection attempt so the
	// next discovery cycle can retry.
	defaultConnectTimeout = 10 * time.Second

	// Socket and bootstrap handshakes use the same bound so reconnects cannot
	// remain parked behind a non-responsive proxy.
	socketHandshakeTimeout = defaultConnectTimeout
)

var errSocketEventsClosed = errors.New("slack socket events channel closed")
var errSocketRunnerStopped = errors.New("slack socket runner stopped")
var errSocketDispatchSaturated = errors.New("slack socket event queue is saturated")

// SlackSocket adapts slack-go's socket-mode client to SocketClient.
//
// Identity verification (design delta 1): slack-go owns the handshake, so
// Start reports the app id straight from the hello frame's
// connection_info.app_id — the same field the Node implementation parsed by
// hand. Reconnects ride slack-go's managed connection, and pings flow
// through whatever dialer proxy is configured, which resolves both the
// hand-rolled backoff ladder and the proxied ping-timeout open item.
type SlackSocket struct {
	appToken       string
	connectTimeout time.Duration
	dialer         *websocket.Dialer
	httpClient     *http.Client
	apiURL         string

	mu          sync.Mutex
	handler     func(SocketEvent)
	stateFn     func(state string, apiErr error)
	cancel      context.CancelFunc
	done        chan struct{}
	maxInFlight int
	stopped     bool
}

func NewSlackSocket(appToken string) *SlackSocket {
	transport := http.DefaultTransport.(*http.Transport).Clone()
	return &SlackSocket{
		appToken:       appToken,
		connectTimeout: defaultConnectTimeout,
		maxInFlight:    defaultSocketMaxInFlight,
		httpClient:     &http.Client{Transport: transport, Timeout: defaultConnectTimeout},
	}
}

// SetMaxInFlight bounds active and queued event callbacks for this connection.
// It must be called before Start.
func (s *SlackSocket) SetMaxInFlight(value int) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.cancel != nil {
		return
	}
	s.maxInFlight = max(1, value)
}

// SetProxy routes both Socket Mode bootstrap HTTP and WebSocket traffic through
// the same proxy.
func (s *SlackSocket) SetProxy(proxyURL *url.URL) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.cancel != nil {
		return
	}
	transport := http.DefaultTransport.(*http.Transport).Clone()
	transport.Proxy = http.ProxyURL(proxyURL)
	s.dialer = &websocket.Dialer{
		Proxy:            http.ProxyURL(proxyURL),
		HandshakeTimeout: socketHandshakeTimeout,
	}
	s.httpClient = &http.Client{Transport: transport, Timeout: defaultConnectTimeout}
}

func (s *SlackSocket) OnEvent(handler func(SocketEvent)) {
	s.mu.Lock()
	s.handler = handler
	s.mu.Unlock()
}

// OnState receives connection-state transitions; the adapter logs them.
// The callback must not call Disconnect synchronously because it runs on a
// socket pump or runner goroutine that Disconnect joins.
func (s *SlackSocket) OnState(handler func(state string, apiErr error)) {
	s.mu.Lock()
	s.stateFn = handler
	s.mu.Unlock()
}

// Start connects once and returns the verified app identity from the hello
// frame. A connection failure before the hello fails fast so discovery can
// retry the target later.
func (s *SlackSocket) Start(ctx context.Context) (string, error) {
	s.mu.Lock()
	if s.cancel != nil || s.stopped {
		s.mu.Unlock()
		return "", errors.New("slack socket was already started")
	}
	runCtx, cancel := context.WithCancel(context.WithoutCancel(ctx))
	done := make(chan struct{})
	s.cancel = cancel
	s.done = done
	handler := s.handler
	stateFn := s.stateFn
	maxInFlight := max(1, s.maxInFlight)
	dialer := s.dialer
	httpClient := s.httpClient
	apiURL := s.apiURL
	appToken := s.appToken
	s.mu.Unlock()

	options := []socketmode.Option{}
	if dialer != nil {
		options = append(options, socketmode.OptionDialer(dialer))
	}
	apiOptions := []slack.Option{slack.OptionAppLevelToken(appToken)}
	if httpClient != nil {
		apiOptions = append(apiOptions, slack.OptionHTTPClient(httpClient))
	}
	if apiURL != "" {
		apiOptions = append(apiOptions, slack.OptionAPIURL(apiURL))
	}
	client := socketmode.New(slack.New("", apiOptions...), options...)

	runner := make(chan error, 1)
	runnerDone := make(chan struct{})
	var runnerResultMu sync.Mutex
	var runnerResult error
	readRunnerResult := func() error {
		runnerResultMu.Lock()
		defer runnerResultMu.Unlock()
		return runnerResult
	}
	go func() {
		err := client.RunContext(runCtx)
		runnerResultMu.Lock()
		runnerResult = err
		runnerResultMu.Unlock()
		close(runnerDone)
		runner <- err
	}()

	hello := make(chan string, 1)
	failed := make(chan error, 1)
	connected := make(chan struct{})
	timeout := time.NewTimer(s.connectTimeout)
	defer timeout.Stop()

	monitorDone := make(chan struct{})
	go func() {
		defer close(monitorDone)
		monitorSocketRunner(runCtx, cancel, runner, connected, stateFn, failed)
	}()
	executor := newSocketEventExecutor(runCtx, maxInFlight, handler)
	go func() {
		runSocketEventPump(
			runCtx,
			client.Events,
			func(ctx context.Context, envelopeID string) error {
				return client.AckCtx(ctx, envelopeID, nil)
			},
			stateFn,
			hello,
			failed,
			connected,
			executor.TryDispatch,
		)
		cancel()
		executor.Wait()
		<-runnerDone
		<-monitorDone
		close(done)
	}()

	select {
	case <-ctx.Done():
		cancel()
		return "", ctx.Err()
	case <-timeout.C:
		cancel()
		return "", errors.New("timed out waiting for the Slack socket hello")
	case err := <-failed:
		cancel()
		return "", err
	case <-done:
		select {
		case err := <-failed:
			return "", err
		default:
		}
		if err := ctx.Err(); err != nil {
			return "", err
		}
		err := readRunnerResult()
		if err == nil {
			err = errSocketRunnerStopped
		}
		return "", err
	case appID := <-hello:
		if err := ctx.Err(); err != nil {
			cancel()
			return "", err
		}
		select {
		case <-runnerDone:
			err := readRunnerResult()
			if err == nil {
				err = errSocketRunnerStopped
			}
			cancel()
			return "", err
		default:
		}
		select {
		case err := <-failed:
			cancel()
			return "", err
		default:
		}
		if err := runCtx.Err(); err != nil {
			return "", err
		}
		return appID, nil
	}
}

type socketEventExecutor struct {
	ctx          context.Context
	messages     chan SocketEvent
	interactions chan SocketEvent
	done         chan struct{}
}

func newSocketEventExecutor(ctx context.Context, maxInFlight int, handler func(SocketEvent)) *socketEventExecutor {
	maxInFlight = max(1, maxInFlight)
	executor := &socketEventExecutor{
		ctx:          ctx,
		messages:     make(chan SocketEvent, maxInFlight),
		interactions: make(chan SocketEvent, maxInFlight),
		done:         make(chan struct{}),
	}
	var workers sync.WaitGroup
	startWorkers := func(queue <-chan SocketEvent) {
		workers.Add(maxInFlight)
		for range maxInFlight {
			go func() {
				defer workers.Done()
				for {
					select {
					case <-ctx.Done():
						return
					case event := <-queue:
						if ctx.Err() != nil {
							return
						}
						if handler != nil {
							handler(event)
						} else {
							event.Ack()
						}
					}
				}
			}()
		}
	}
	startWorkers(executor.messages)
	startWorkers(executor.interactions)
	go func() {
		workers.Wait()
		close(executor.done)
	}()
	return executor
}

func (e *socketEventExecutor) TryDispatch(event SocketEvent) bool {
	queue := e.messages
	if IsSlackInteraction(event.Body) {
		queue = e.interactions
	}
	select {
	case queue <- event:
		return true
	case <-e.ctx.Done():
		return false
	default:
		return false
	}
}

func (e *socketEventExecutor) Wait() { <-e.done }

func monitorSocketRunner(
	ctx context.Context,
	cancel context.CancelFunc,
	runner <-chan error,
	connected <-chan struct{},
	stateFn func(state string, apiErr error),
	failed chan<- error,
) {
	select {
	case <-ctx.Done():
		return
	case err := <-runner:
		if ctx.Err() != nil {
			return
		}
		if err == nil {
			err = errSocketRunnerStopped
		}
		select {
		case <-connected:
			reportState(stateFn, "error", err)
		default:
			select {
			case failed <- err:
			default:
			}
		}
		cancel()
	}
}

func runSocketEventPump(
	ctx context.Context,
	events <-chan socketmode.Event,
	ack func(context.Context, string) error,
	stateFn func(state string, apiErr error),
	hello chan<- string,
	failed chan<- error,
	connected chan struct{},
	dispatch func(SocketEvent) bool,
) {
	forwarding := false
	for {
		if ctx.Err() != nil {
			return
		}
		select {
		case <-ctx.Done():
			return
		case evt, ok := <-events:
			if ctx.Err() != nil {
				return
			}
			if !ok {
				if forwarding {
					reportState(stateFn, "error", errSocketEventsClosed)
				} else {
					select {
					case failed <- errSocketEventsClosed:
					default:
					}
				}
				return
			}
			switch evt.Type {
			case socketmode.EventTypeConnecting:
				reportState(stateFn, "connecting", nil)
			case socketmode.EventTypeConnected, socketmode.EventTypeHello:
				reportState(stateFn, "connected", nil)
				if evt.Type == socketmode.EventTypeHello && !forwarding {
					forwarding = true
					close(connected)
					appID := ""
					if evt.Request != nil {
						appID = evt.Request.ConnectionInfo.AppID
					}
					select {
					case hello <- appID:
					default:
					}
				}
			case socketmode.EventTypeConnectionError, socketmode.EventTypeInvalidAuth:
				var apiErr error
				if asErr, ok := evt.Data.(error); ok {
					apiErr = asErr
				}
				if evt.Type == socketmode.EventTypeInvalidAuth && apiErr == nil {
					apiErr = errors.New("slack rejected the app token")
				}
				reportState(stateFn, "reconnecting", apiErr)
				if !forwarding {
					select {
					case failed <- orUnavailable(apiErr):
					default:
					}
					return
				}
			case socketmode.EventTypeEventsAPI, socketmode.EventTypeInteractive:
				if !forwarding || evt.Request == nil {
					continue
				}
				var body any
				if err := json.Unmarshal(evt.Request.Payload, &body); err != nil {
					continue // malformed payloads are dropped unacked
				}
				envelopeID := evt.Request.EnvelopeID
				event := SocketEvent{
					Context: ctx,
					Body:    body,
					Ack: func() {
						_ = ack(ctx, envelopeID)
					},
				}
				// Slack retries unacknowledged Socket Mode envelopes. Rejecting
				// admission preserves bounded memory without blocking later
				// interactions behind a saturated message lane.
				if !dispatch(event) {
					reportState(stateFn, "backpressured", errSocketDispatchSaturated)
				}
			default:
				// ping/disconnect internals stay with slack-go.
			}
		}
	}
}

func reportState(fn func(state string, apiErr error), state string, apiErr error) {
	if fn != nil {
		fn(state, apiErr)
	}
}

func orUnavailable(apiErr error) error {
	if apiErr != nil {
		return apiErr
	}
	return errors.New("slack socket connection failed")
}

// Disconnect tears down the managed connection; slack-go's run loop exits
// on context cancellation.
func (s *SlackSocket) Disconnect(ctx context.Context) error {
	s.mu.Lock()
	if !s.stopped {
		s.stopped = true
	}
	cancel := s.cancel
	done := s.done
	httpClient := s.httpClient
	s.mu.Unlock()
	if httpClient != nil {
		defer httpClient.CloseIdleConnections()
	}
	if cancel != nil {
		cancel()
	}
	if done != nil {
		select {
		case <-done:
		case <-ctx.Done():
			return ctx.Err()
		}
	}
	return nil
}

// SlackWeb implements WebClient over slack-go plus one thin form caller.
//
// chat.postMessage and chat.update go through the thin caller because
// slack-go does not expose client_msg_id — the key statusDispatchRef
// lookups match history by. Reactions, history, and uploads use typed
// methods; the v0.29 upload response carries only a file id, so upload
// identities always resolve through the history scan.
type SlackWeb struct {
	token      string
	httpClient *http.Client
	apiBaseURL string
	api        *slack.Client
}

func NewSlackWeb(botToken string, httpClient *http.Client) *SlackWeb {
	return newSlackWebWithAPIBaseURL(botToken, httpClient, slackAPIBaseURL)
}

func newSlackWebWithAPIBaseURL(botToken string, httpClient *http.Client, apiBaseURL string) *SlackWeb {
	if strings.TrimSpace(apiBaseURL) == "" {
		apiBaseURL = slackAPIBaseURL
	}
	apiBaseURL = strings.TrimRight(apiBaseURL, "/") + "/"
	return &SlackWeb{
		token:      botToken,
		httpClient: httpClient,
		apiBaseURL: apiBaseURL,
		api: slack.New(
			botToken,
			slack.OptionHTTPClient(httpClient),
			slack.OptionAPIURL(apiBaseURL),
		),
	}
}

func (w *SlackWeb) PostMessage(ctx context.Context, input PostMessageInput) (string, error) {
	form := url.Values{"channel": {input.Channel}, "text": {input.Text}}
	if input.ThreadTs != "" {
		form.Set("thread_ts", input.ThreadTs)
	}
	if input.ClientMsgID != "" {
		form.Set("client_msg_id", input.ClientMsgID)
	}
	if len(input.Blocks) > 0 {
		blocks, err := json.Marshal(input.Blocks)
		if err != nil {
			return "", fmt.Errorf("blocks were not serializable: %w", err)
		}
		form.Set("blocks", string(blocks))
	}
	return w.callChat(ctx, "chat.postMessage", form)
}

func (w *SlackWeb) UpdateMessage(ctx context.Context, input UpdateMessageInput) (string, error) {
	form := url.Values{"channel": {input.Channel}, "ts": {input.TS}, "text": {input.Text}}
	if len(input.Blocks) > 0 {
		blocks, err := json.Marshal(input.Blocks)
		if err != nil {
			return "", fmt.Errorf("blocks were not serializable: %w", err)
		}
		form.Set("blocks", string(blocks))
	}
	return w.callChat(ctx, "chat.update", form)
}

// callChat posts one chat.* form request and normalizes the envelope:
// ok:false decays into a coded SlackError, transport failures propagate.
func (w *SlackWeb) callChat(ctx context.Context, method string, form url.Values) (string, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, w.apiBaseURL+method, strings.NewReader(form.Encode()))
	if err != nil {
		return "", err
	}
	req.Header.Set("Authorization", "Bearer "+w.token)
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	resp, err := w.httpClient.Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()
	raw, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if err != nil {
		return "", err
	}
	var parsed struct {
		OK    bool   `json:"ok"`
		Error string `json:"error"`
		TS    string `json:"ts"`
	}
	if err := json.Unmarshal(raw, &parsed); err != nil {
		return "", fmt.Errorf("slack %s returned an invalid response (%d)", method, resp.StatusCode)
	}
	if !parsed.OK {
		code := parsed.Error
		if code == "" {
			code = fmt.Sprintf("http_%d", resp.StatusCode)
		}
		return "", &SlackError{Code: code}
	}
	return parsed.TS, nil
}

func (w *SlackWeb) AddReaction(ctx context.Context, channel, name, timestamp string) error {
	return w.api.AddReactionContext(ctx, name, slack.NewRefToMessage(channel, timestamp))
}

func (w *SlackWeb) RemoveReaction(ctx context.Context, channel, name, timestamp string) error {
	return w.api.RemoveReactionContext(ctx, name, slack.NewRefToMessage(channel, timestamp))
}

func (w *SlackWeb) GetReactions(ctx context.Context, channel, timestamp string) ([]string, error) {
	item, err := w.api.GetReactionsContext(ctx, slack.NewRefToMessage(channel, timestamp), slack.GetReactionsParameters{Full: true})
	if err != nil {
		return nil, err
	}
	names := make([]string, 0, len(item.Reactions))
	for _, reaction := range item.Reactions {
		names = append(names, reaction.Name)
	}
	return names, nil
}

func (w *SlackWeb) GetConversationHistory(ctx context.Context, input HistoryInput) (HistoryPage, error) {
	resp, err := w.api.GetConversationHistoryContext(ctx, &slack.GetConversationHistoryParameters{
		ChannelID: input.Channel,
		Cursor:    input.Cursor,
		Latest:    input.Latest,
		Oldest:    input.Oldest,
		Inclusive: input.Latest != "" || input.Oldest != "",
		Limit:     input.Limit,
	})
	if err != nil {
		return HistoryPage{}, err
	}
	messages := make([]HistoryMessage, 0, len(resp.Messages))
	for _, message := range resp.Messages {
		historyMessage := HistoryMessage{
			TS:          message.Timestamp,
			ClientMsgID: message.ClientMsgID,
			Text:        message.Text,
		}
		for _, file := range message.Files {
			historyMessage.FileIDs = append(historyMessage.FileIDs, file.ID)
		}
		messages = append(messages, historyMessage)
	}
	return HistoryPage{
		Messages:   messages,
		HasMore:    resp.HasMore,
		NextCursor: resp.ResponseMetaData.NextCursor,
	}, nil
}

func (w *SlackWeb) UploadFileV2(ctx context.Context, input FileUploadInput) (FileUploadResult, error) {
	params := slack.UploadFileParameters{
		Filename:       input.Filename,
		FileSize:       len(input.Content),
		Reader:         bytes.NewReader(input.Content),
		InitialComment: input.InitialComment,
	}
	if input.ThreadTs != "" {
		params.Channels = []string{input.ChannelID}
		params.ThreadTimestamp = input.ThreadTs
	} else {
		params.Channel = input.ChannelID
	}
	summary, err := w.api.UploadFileContext(ctx, params)
	if err != nil {
		return FileUploadResult{}, err
	}
	result := FileUploadResult{}
	if summary != nil {
		result.FileID = summary.ID
	}
	return result, nil
}
