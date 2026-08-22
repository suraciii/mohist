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
	slackAPIBaseURL = "https://slack.com/api/"

	// defaultConnectTimeout bounds how long Start waits for the Socket Mode
	// hello; a dead endpoint fails the target's connection attempt so the
	// next discovery cycle can retry.
	defaultConnectTimeout = 10 * time.Second

	// socketHandshakeTimeout matches slack-go's own default handshake bound.
	socketHandshakeTimeout = 45 * time.Second
)

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

	mu      sync.Mutex
	handler func(SocketEvent)
	stateFn func(state string, apiErr error)
	cancel  context.CancelFunc
	stopped bool
}

func NewSlackSocket(appToken string) *SlackSocket {
	return &SlackSocket{appToken: appToken, connectTimeout: defaultConnectTimeout}
}

// SetProxy routes the underlying WebSocket dialer through an HTTP proxy.
func (s *SlackSocket) SetProxy(proxyURL *url.URL) {
	s.dialer = &websocket.Dialer{
		Proxy:            http.ProxyURL(proxyURL),
		HandshakeTimeout: socketHandshakeTimeout,
	}
}

func (s *SlackSocket) OnEvent(handler func(SocketEvent)) {
	s.mu.Lock()
	s.handler = handler
	s.mu.Unlock()
}

// OnState receives connection-state transitions; the adapter logs them.
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
	handler := s.handler
	stateFn := s.stateFn
	s.mu.Unlock()

	options := []socketmode.Option{}
	if s.dialer != nil {
		options = append(options, socketmode.OptionDialer(s.dialer))
	}
	client := socketmode.New(slack.New(s.appToken), options...)

	runCtx, cancel := context.WithCancel(context.WithoutCancel(ctx))
	s.mu.Lock()
	s.cancel = cancel
	s.mu.Unlock()

	go func() { _ = client.RunContext(runCtx) }()

	hello := make(chan string, 1)
	failed := make(chan error, 1)
	timeout := time.NewTimer(s.connectTimeout)
	defer timeout.Stop()

	go func() {
		forwarding := false
		for {
			select {
			case <-runCtx.Done():
				return
			case evt := <-client.Events:
				switch evt.Type {
				case socketmode.EventTypeConnecting:
					reportState(stateFn, "connecting", nil)
				case socketmode.EventTypeConnected, socketmode.EventTypeHello:
					reportState(stateFn, "connected", nil)
					if evt.Type == socketmode.EventTypeHello && !forwarding {
						forwarding = true
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
					if !forwarding {
						continue
					}
					request := evt.Request
					if request == nil {
						continue
					}
					var body any
					if err := json.Unmarshal(request.Payload, &body); err != nil {
						continue // malformed payloads are dropped unacked
					}
					event := SocketEvent{
						Body: body,
						Ack: func() {
							_ = client.AckCtx(runCtx, request.EnvelopeID, nil)
						},
					}
					if handler != nil {
						handler(event)
					} else {
						event.Ack()
					}
				default:
					// ping/disconnect internals stay with slack-go.
				}
			}
		}
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
	case appID := <-hello:
		return appID, nil
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
func (s *SlackSocket) Disconnect(context.Context) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if !s.stopped {
		s.stopped = true
		if s.cancel != nil {
			s.cancel()
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
	api        *slack.Client
}

func NewSlackWeb(botToken string, httpClient *http.Client) *SlackWeb {
	return &SlackWeb{
		token:      botToken,
		httpClient: httpClient,
		api:        slack.New(botToken, slack.OptionHTTPClient(httpClient)),
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
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, slackAPIBaseURL+method, strings.NewReader(form.Encode()))
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

func (w *SlackWeb) GetConversationHistory(ctx context.Context, input HistoryInput) ([]HistoryMessage, error) {
	resp, err := w.api.GetConversationHistoryContext(ctx, &slack.GetConversationHistoryParameters{
		ChannelID: input.Channel,
		Latest:    input.Latest,
		Oldest:    input.Oldest,
		Inclusive: input.Latest != "" || input.Oldest != "",
		Limit:     input.Limit,
	})
	if err != nil {
		return nil, err
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
	return messages, nil
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
