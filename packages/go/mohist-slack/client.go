package mohistslack

import (
	"context"
	"regexp"
)

// SocketEvent is one inbound Socket Mode payload. Ack acknowledges the event
// to Slack; the adapter controls when (messages and interactions acknowledge
// after their server action is accepted).
type SocketEvent struct {
	Context context.Context
	Body    any
	Ack     func()
}

// SocketClient is one Slack Socket Mode connection for a single target.
type SocketClient interface {
	// Start connects and returns the verified Slack app identity of the
	// connected app. It returns once the socket is usable.
	Start(ctx context.Context) (string, error)
	// OnEvent registers the callback invoked per inbound event. Production
	// clients bound callback concurrency and cancel Context on disconnect. A
	// callback must not call Disconnect synchronously because disconnect joins
	// all active callbacks.
	OnEvent(handler func(SocketEvent))
	// Disconnect closes the connection and stops background pumps.
	Disconnect(ctx context.Context) error
}

// SocketFactory builds the probe and runtime sockets for one target.
type SocketFactory func(appToken string, target Target) SocketClient

// PostMessageInput is one chat.postMessage call.
type PostMessageInput struct {
	Channel     string
	Text        string
	ThreadTs    string
	ClientMsgID string
	Blocks      []map[string]any
}

// UpdateMessageInput is one chat.update call.
type UpdateMessageInput struct {
	Channel string
	TS      string
	Text    string
	Blocks  []map[string]any
}

// HistoryInput scopes one conversations.history call.
type HistoryInput struct {
	Channel string
	Latest  string
	Oldest  string
	Limit   int
}

// HistoryMessage is one message from conversations.history.
type HistoryMessage struct {
	TS          string
	ClientMsgID string
	Text        string
	FileIDs     []string
}

// FileUploadInput is one filesUploadV2 call. ChannelID alone uploads to the
// conversation; ThreadTs switches to channels+thread_ts semantics.
type FileUploadInput struct {
	ChannelID      string
	ThreadTs       string
	Filename       string
	Content        []byte
	InitialComment string
}

// FileUploadResult locates the share a completed upload produced.
type FileUploadResult struct {
	FileID         string
	PublicShareTS  string
	PrivateShareTS string
}

// WebClient speaks the subset of the Slack Web API the adapter uses.
//
// Methods return an error carrying a recognizable Slack rejection through
// SlackError or an "API error occurred: <code>" message; other errors are
// transport failures. This mirrors the Node split between {ok:false}
// responses and thrown exceptions.
type WebClient interface {
	PostMessage(ctx context.Context, input PostMessageInput) (ts string, err error)
	UpdateMessage(ctx context.Context, input UpdateMessageInput) (ts string, err error)
	AddReaction(ctx context.Context, channel, name, timestamp string) error
	RemoveReaction(ctx context.Context, channel, name, timestamp string) error
	GetReactions(ctx context.Context, channel, timestamp string) (names []string, err error)
	GetConversationHistory(ctx context.Context, input HistoryInput) ([]HistoryMessage, error)
	UploadFileV2(ctx context.Context, input FileUploadInput) (FileUploadResult, error)
}

// WebFactory builds the web client for one runtime lease's bot token.
type WebFactory func(botToken string, target Target) WebClient

// SlackError is a structured Slack API rejection.
type SlackError struct {
	Code string
}

// Error implements error.
func (e *SlackError) Error() string { return "slack: " + e.Code }

var (
	apiErrorCodePattern = regexp.MustCompile(`API error occurred:\s*([a-z][a-z0-9_]*)`)
	bareCodePattern     = regexp.MustCompile(`^[a-z][a-z0-9_]*$`)
)

// SlackErrorCode normalizes any error into a Slack rejection code: typed
// SlackError values first, then the "API error occurred: <code>" message
// pattern, then slack-go's habit of returning the bare snake_case rejection
// code as the whole error message. An empty result means the error carries
// no Slack code and must not drive degradation decisions.
func SlackErrorCode(err error) string {
	if err == nil {
		return ""
	}
	var slackErr *SlackError
	if ok := asSlackError(err, &slackErr); ok && slackErr.Code != "" {
		return slackErr.Code
	}
	message := err.Error()
	if match := apiErrorCodePattern.FindStringSubmatch(message); match != nil {
		return match[1]
	}
	if bareCodePattern.MatchString(message) && len(message) <= 64 {
		return message
	}
	return ""
}

// asSlackError unwraps *SlackError through wrapped error chains.
func asSlackError(err error, target **SlackError) bool {
	for err != nil {
		if slackErr, ok := err.(*SlackError); ok {
			*target = slackErr
			return true
		}
		unwrapper, ok := err.(interface{ Unwrap() error })
		if !ok {
			return false
		}
		err = unwrapper.Unwrap()
	}
	return false
}

// unsupportedReactionCodes lists the reaction rejections that degrade instead
// of retrying, per design/slack-go-port.md.
var unsupportedReactionCodes = map[string]bool{
	"cant_react":             true,
	"message_not_found":      true,
	"not_in_channel":         true,
	"not_allowed_token_type": true,
	"invalid_timestamp":      true,
	"channel_not_found":      true,
	"missing_scope":          true,
}

// IsUnsupportedReactionError reports whether a reaction rejection degrades
// rather than retries.
func IsUnsupportedReactionError(code string) bool {
	return unsupportedReactionCodes[code]
}

// StaleRuntimeError marks that a fencing check found the runtime superseded;
// failures from such flows are swallowed, never acted on.
type StaleRuntimeError struct{}

// Error implements error.
func (StaleRuntimeError) Error() string { return "runtime was replaced" }
