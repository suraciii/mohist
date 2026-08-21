package mohistslack

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
)

// SlackError is a provider-level rejection carrying the Slack error code
// (for example cant_react or missing_scope). Transport failures must not use
// this type; they stay plain errors so callers can tell them apart.
type SlackError struct {
	Code string
}

// Error implements error.
func (e *SlackError) Error() string { return "slack api error: " + e.Code }

// PostOptions carries the optional fields of a Slack chat.postMessage call.
type PostOptions struct {
	ThreadTs    string
	ClientMsgID string
	Blocks      []map[string]any
}

// PostedMessage identifies one message written to Slack.
type PostedMessage struct {
	Ts string
}

// HistoryMessage is one message read back from a conversation.
type HistoryMessage struct {
	Ts          string
	ClientMsgID string
	Text        string
	Files       []string // file ids
}

// ReactionSummary names one reaction present on a message.
type ReactionSummary struct {
	Name string
}

// FileUpload describes one filesUploadV2 call.
type FileUpload struct {
	ChannelID      string
	ThreadTs       string
	FileName       string
	Content        []byte
	InitialComment string
}

// UploadResult reports where Slack stored an uploaded file.
type UploadResult struct {
	FileID  string
	ShareTs string
}

// HistoryQuery selects a conversation history window. AroundTs pins the
// window to one timestamp; empty reads the most recent page.
type HistoryQuery struct {
	Channel  string
	AroundTs string
}

// SlackClient is the write side of the Slack Web API used by deliveries.
type SlackClient interface {
	PostMessage(ctx context.Context, channel, text string, opts PostOptions) (*PostedMessage, error)
	UpdateMessage(ctx context.Context, channel, ts, text string, blocks []map[string]any) (*PostedMessage, error)
	AddReaction(ctx context.Context, channel, name, timestamp string) error
	RemoveReaction(ctx context.Context, channel, name, timestamp string) error
	Reactions(ctx context.Context, channel, timestamp string) ([]ReactionSummary, error)
	UploadFile(ctx context.Context, upload FileUpload) (*UploadResult, error)
	History(ctx context.Context, query HistoryQuery) ([]HistoryMessage, error)
}

// DeliveryPayload is the JSON object stored in Delivery.PayloadJSON.
type DeliveryPayload struct {
	Operation               string           `json:"operation,omitempty"`
	Text                    string           `json:"text,omitempty"`
	ClientMessageID         string           `json:"clientMessageId,omitempty"`
	ProviderMessageIdentity *MessageIdentity `json:"providerMessageIdentity,omitempty"`
	TargetMessageIdentity   *MessageIdentity `json:"targetMessageIdentity,omitempty"`
	Reaction                string           `json:"reaction,omitempty"`
	FallbackText            string           `json:"fallbackText,omitempty"`
	FallbackDispatchRef     string           `json:"fallbackDispatchRef,omitempty"`
	StatusDispatchRef       string           `json:"statusDispatchRef,omitempty"`
	Blocks                  []map[string]any `json:"blocks,omitempty"`
	FileName                string           `json:"fileName,omitempty"`
	FileContentBase64       string           `json:"fileContentBase64,omitempty"`
	Segments                []string         `json:"segments,omitempty"`
}

// ParseDeliveryPayload decodes and validates one delivery payload.
func ParseDeliveryPayload(payloadJSON string) (DeliveryPayload, error) {
	var probe any
	if err := json.Unmarshal([]byte(payloadJSON), &probe); err != nil {
		return DeliveryPayload{}, errors.New("Delivery payload was not an object")
	}
	if _, ok := probe.(map[string]any); !ok {
		return DeliveryPayload{}, errors.New("Delivery payload was not an object")
	}
	var payload DeliveryPayload
	if err := json.Unmarshal([]byte(payloadJSON), &payload); err != nil {
		return DeliveryPayload{}, errors.New("Delivery payload was not an object")
	}
	return payload, nil
}

func requiredText(payload DeliveryPayload) (string, error) {
	if payload.Text == "" {
		return "", errors.New("Delivery payload did not contain text")
	}
	return payload.Text, nil
}

// DeliveredAck builds a delivered acknowledgment for one delivery.
func DeliveredAck(delivery *Delivery, identity *MessageIdentity) DeliveryAck {
	return DeliveryAck{ID: delivery.ID, Outcome: OutcomeDelivered, ProviderMessageIdentity: identity}
}

func retryAck(delivery *Delivery, reason string) DeliveryAck {
	return DeliveryAck{ID: delivery.ID, Outcome: OutcomeRetry, Reason: reason}
}

func uncertainAck(delivery *Delivery, reason string) DeliveryAck {
	return DeliveryAck{ID: delivery.ID, Outcome: OutcomeUncertain, Reason: reason}
}

// WithAdapterID stamps the adapter identity onto an acknowledgment.
func WithAdapterID(ack DeliveryAck, adapterID string) DeliveryAck {
	ack.AdapterID = adapterID
	return ack
}

var unsupportedReactionCodes = map[string]bool{
	"cant_react":             true,
	"message_not_found":      true,
	"not_in_channel":         true,
	"not_allowed_token_type": true,
	"invalid_timestamp":      true,
	"channel_not_found":      true,
	"missing_scope":          true,
}

func isKnownDeliveryOperation(operation string) bool {
	switch operation {
	case "post_message", "chat_update", "reaction_add", "reaction_remove", "upload_file":
		return true
	default:
		return false
	}
}

// slackCode extracts the provider error code from a failure. The second
// result is false for transport-level failures that carry no code.
func slackCode(err error) (string, bool) {
	var slackErr *SlackError
	if errors.As(err, &slackErr) && slackErr.Code != "" {
		return slackErr.Code, true
	}
	return "", false
}

// MutateDelivery performs one delivery against Slack and settles it. Errors
// returned to the caller are operational (stale runtime, malformed payload);
// provider rejections are folded into retry/uncertain acknowledgments.
func MutateDelivery(ctx context.Context, client SlackClient, delivery *Delivery, ensureCurrent func() error) (DeliveryAck, error) {
	if err := ensureCurrent(); err != nil {
		return DeliveryAck{}, err
	}
	payload, err := ParseDeliveryPayload(delivery.PayloadJSON)
	if err != nil {
		return DeliveryAck{}, err
	}
	operation := payload.Operation
	if operation == "" {
		operation = "post_message"
	}
	if !isKnownDeliveryOperation(operation) {
		return ReconcileDelivery(ctx, client, delivery, ensureCurrent)
	}
	if len(payload.Segments) > 1 {
		return deliverSegments(ctx, client, delivery, payload, ensureCurrent)
	}
	switch operation {
	case "chat_update":
		return mutateChatUpdate(ctx, client, delivery, payload, ensureCurrent)
	case "reaction_add", "reaction_remove":
		return mutateReaction(ctx, client, delivery, payload, operation, ensureCurrent)
	case "upload_file":
		return mutateUploadFile(ctx, client, delivery, payload, ensureCurrent)
	default:
		return mutatePostMessage(ctx, client, delivery, payload, ensureCurrent)
	}
}

func mutatePostMessage(ctx context.Context, client SlackClient, delivery *Delivery, payload DeliveryPayload, ensureCurrent func() error) (DeliveryAck, error) {
	text := payload.Text
	if text == "" && len(payload.Blocks) == 0 {
		var err error
		if text, err = requiredText(payload); err != nil {
			return DeliveryAck{}, err
		}
	}
	if payload.StatusDispatchRef != "" {
		existing, err := findStatusMessage(ctx, client, delivery.ConversationID, payload.StatusDispatchRef, ensureCurrent)
		if err != nil {
			return DeliveryAck{}, err
		}
		if existing != nil {
			if err := ensureCurrent(); err != nil {
				return DeliveryAck{}, err
			}
			response, err := client.UpdateMessage(ctx, existing.ConversationID, existing.MessageTs, text, payload.Blocks)
			if err != nil {
				if code, ok := slackCode(err); ok {
					return retryAck(delivery, code), nil
				}
				return DeliveryAck{}, err
			}
			ts := existing.MessageTs
			if response.Ts != "" {
				ts = response.Ts
			}
			return DeliveredAck(delivery, &MessageIdentity{ConversationID: existing.ConversationID, MessageTs: ts}), nil
		}
	}
	if err := ensureCurrent(); err != nil {
		return DeliveryAck{}, err
	}
	response, err := client.PostMessage(ctx, delivery.ConversationID, text, PostOptions{
		ThreadTs:    threadTsOf(delivery),
		ClientMsgID: payload.ClientMessageID,
		Blocks:      payload.Blocks,
	})
	if err != nil {
		if code, ok := slackCode(err); ok {
			return retryAck(delivery, code), nil
		}
		return DeliveryAck{}, err
	}
	var identity *MessageIdentity
	if response.Ts != "" {
		identity = &MessageIdentity{ConversationID: delivery.ConversationID, MessageTs: response.Ts}
	}
	return DeliveredAck(delivery, identity), nil
}

func mutateChatUpdate(ctx context.Context, client SlackClient, delivery *Delivery, payload DeliveryPayload, ensureCurrent func() error) (DeliveryAck, error) {
	target := payload.ProviderMessageIdentity
	if target == nil {
		return DeliveryAck{}, errors.New("chat.update delivery has no provider message identity")
	}
	text, err := requiredText(payload)
	if err != nil {
		return DeliveryAck{}, err
	}
	if err := ensureCurrent(); err != nil {
		return DeliveryAck{}, err
	}
	response, err := client.UpdateMessage(ctx, target.ConversationID, target.MessageTs, text, payload.Blocks)
	if err != nil {
		code, ok := slackCode(err)
		if !ok {
			return DeliveryAck{}, err
		}
		if payload.FallbackText == "" || payload.FallbackDispatchRef == "" {
			return retryAck(delivery, code), nil
		}
		return postFallback(ctx, client, delivery, payload, code, ensureCurrent)
	}
	ts := target.MessageTs
	if response.Ts != "" {
		ts = response.Ts
	}
	return DeliveredAck(delivery, &MessageIdentity{ConversationID: target.ConversationID, MessageTs: ts}), nil
}

func mutateReaction(ctx context.Context, client SlackClient, delivery *Delivery, payload DeliveryPayload, operation string, ensureCurrent func() error) (DeliveryAck, error) {
	target := payload.TargetMessageIdentity
	if target == nil || payload.Reaction == "" {
		return DeliveryAck{}, fmt.Errorf("%s delivery is missing its target", operation)
	}
	if err := ensureCurrent(); err != nil {
		return DeliveryAck{}, err
	}
	err := applyReaction(ctx, client, operation, target, payload.Reaction)
	if err == nil {
		return DeliveredAck(delivery, nil), nil
	}
	code, ok := slackCode(err)
	if !ok {
		return DeliveryAck{}, err
	}
	if !unsupportedReactionCodes[code] {
		return retryAck(delivery, code), nil
	}
	if code == "missing_scope" {
		if payload.FallbackText == "" || payload.FallbackDispatchRef == "" {
			return DeliveredAck(delivery, nil), nil
		}
		return postFallback(ctx, client, delivery, payload, code, ensureCurrent)
	}
	if payload.StatusDispatchRef != "" {
		status, err := findStatusMessage(ctx, client, delivery.ConversationID, payload.StatusDispatchRef, ensureCurrent)
		if err != nil {
			return DeliveryAck{}, err
		}
		if status != nil && status.MessageTs != target.MessageTs {
			if err := ensureCurrent(); err != nil {
				return DeliveryAck{}, err
			}
			retryErr := applyReaction(ctx, client, operation, status, payload.Reaction)
			if retryErr == nil {
				return DeliveredAck(delivery, nil), nil
			}
			retryCode, ok := slackCode(retryErr)
			if !ok {
				return DeliveryAck{}, retryErr
			}
			if !unsupportedReactionCodes[retryCode] {
				return retryAck(delivery, retryCode), nil
			}
		}
	}
	if operation == "reaction_remove" {
		return DeliveredAck(delivery, nil), nil
	}
	if payload.FallbackText != "" && payload.FallbackDispatchRef != "" {
		return postFallback(ctx, client, delivery, payload, code, ensureCurrent)
	}
	return DeliveredAck(delivery, nil), nil
}

func applyReaction(ctx context.Context, client SlackClient, operation string, target *MessageIdentity, reaction string) error {
	if operation == "reaction_add" {
		return client.AddReaction(ctx, target.ConversationID, reaction, target.MessageTs)
	}
	return client.RemoveReaction(ctx, target.ConversationID, reaction, target.MessageTs)
}

func mutateUploadFile(ctx context.Context, client SlackClient, delivery *Delivery, payload DeliveryPayload, ensureCurrent func() error) (DeliveryAck, error) {
	if payload.FileName == "" || payload.FileContentBase64 == "" {
		return DeliveryAck{}, errors.New("upload_file delivery is missing the file payload")
	}
	content, err := base64.StdEncoding.DecodeString(payload.FileContentBase64)
	if err != nil {
		return DeliveryAck{}, errors.New("upload_file delivery carries invalid base64 content")
	}
	if err := ensureCurrent(); err != nil {
		return DeliveryAck{}, err
	}
	result, err := client.UploadFile(ctx, FileUpload{
		ChannelID:      delivery.ConversationID,
		ThreadTs:       threadTsOf(delivery),
		FileName:       payload.FileName,
		Content:        content,
		InitialComment: payload.Text,
	})
	if err != nil {
		if code, ok := slackCode(err); ok {
			return retryAck(delivery, code), nil
		}
		return DeliveryAck{}, err
	}
	identity := shareIdentity(delivery, result)
	if identity == nil && result.FileID != "" {
		messages, err := client.History(ctx, HistoryQuery{Channel: delivery.ConversationID})
		if err != nil {
			return DeliveryAck{}, err
		}
	scan:
		for _, message := range messages {
			for _, fileID := range message.Files {
				if fileID == result.FileID && message.Ts != "" {
					identity = &MessageIdentity{ConversationID: delivery.ConversationID, MessageTs: message.Ts}
					break scan
				}
			}
		}
	}
	return DeliveredAck(delivery, identity), nil
}

func shareIdentity(delivery *Delivery, result *UploadResult) *MessageIdentity {
	if result.ShareTs == "" {
		return nil
	}
	return &MessageIdentity{ConversationID: delivery.ConversationID, MessageTs: result.ShareTs}
}

func deliverSegments(ctx context.Context, client SlackClient, delivery *Delivery, payload DeliveryPayload, ensureCurrent func() error) (DeliveryAck, error) {
	threadTs := threadTsOf(delivery)
	var first *MessageIdentity
	for index, segment := range payload.Segments {
		if err := ensureCurrent(); err != nil {
			return DeliveryAck{}, err
		}
		options := PostOptions{ThreadTs: threadTs}
		if index == 0 {
			options.ClientMsgID = payload.ClientMessageID
		}
		response, err := client.PostMessage(ctx, delivery.ConversationID, segment, options)
		if err != nil {
			if code, ok := slackCode(err); ok {
				return retryAck(delivery, code), nil
			}
			return DeliveryAck{}, err
		}
		if index == 0 && response.Ts != "" {
			first = &MessageIdentity{ConversationID: delivery.ConversationID, MessageTs: response.Ts}
		}
	}
	return DeliveredAck(delivery, first), nil
}

func findStatusMessage(ctx context.Context, client SlackClient, conversationID, dispatchRef string, ensureCurrent func() error) (*MessageIdentity, error) {
	if err := ensureCurrent(); err != nil {
		return nil, err
	}
	messages, err := client.History(ctx, HistoryQuery{Channel: conversationID})
	if err != nil {
		return nil, err
	}
	for _, message := range messages {
		if message.ClientMsgID == dispatchRef && message.Ts != "" {
			return &MessageIdentity{ConversationID: conversationID, MessageTs: message.Ts}, nil
		}
	}
	return nil, nil
}

func postFallback(ctx context.Context, client SlackClient, delivery *Delivery, payload DeliveryPayload, reason string, ensureCurrent func() error) (DeliveryAck, error) {
	if err := ensureCurrent(); err != nil {
		return DeliveryAck{}, err
	}
	fallbackText := payload.FallbackText
	if fallbackText == "" {
		fallbackText = payload.Text
	}
	response, err := client.PostMessage(ctx, delivery.ConversationID, fallbackText, PostOptions{
		ThreadTs:    threadTsOf(delivery),
		ClientMsgID: payload.FallbackDispatchRef,
		Blocks:      payload.Blocks,
	})
	if err != nil {
		if code, ok := slackCode(err); ok {
			return retryAck(delivery, code), nil
		}
		return DeliveryAck{}, err
	}
	var identity *MessageIdentity
	if response.Ts != "" {
		identity = &MessageIdentity{ConversationID: delivery.ConversationID, MessageTs: response.Ts}
	}
	return DeliveredAck(delivery, identity), nil
}

// ReconcileDelivery settles an uncertain delivery against provider state.
func ReconcileDelivery(ctx context.Context, client SlackClient, delivery *Delivery, ensureCurrent func() error) (DeliveryAck, error) {
	if err := ensureCurrent(); err != nil {
		return DeliveryAck{}, err
	}
	payload, err := ParseDeliveryPayload(delivery.PayloadJSON)
	if err != nil {
		return DeliveryAck{}, err
	}
	target := payload.ProviderMessageIdentity
	if target == nil {
		target = payload.TargetMessageIdentity
	}
	if (payload.Operation == "reaction_add" || payload.Operation == "reaction_remove") && target != nil && payload.Reaction != "" {
		if err := ensureCurrent(); err != nil {
			return DeliveryAck{}, err
		}
		summaries, err := client.Reactions(ctx, target.ConversationID, target.MessageTs)
		if err != nil {
			code, ok := slackCode(err)
			if !ok {
				return DeliveryAck{}, err
			}
			if !unsupportedReactionCodes[code] {
				return uncertainAck(delivery, code), nil
			}
			if payload.FallbackText == "" || payload.FallbackDispatchRef == "" {
				return DeliveredAck(delivery, target), nil
			}
			return postFallback(ctx, client, delivery, payload, code, ensureCurrent)
		}
		present := false
		for _, summary := range summaries {
			if summary.Name == payload.Reaction {
				present = true
			}
		}
		deliveredState := present
		if payload.Operation == "reaction_remove" {
			deliveredState = !present
		}
		if deliveredState {
			return DeliveredAck(delivery, target), nil
		}
		return retryAck(delivery, "provider_mutation_absent"), nil
	}
	if err := ensureCurrent(); err != nil {
		return DeliveryAck{}, err
	}
	query := HistoryQuery{Channel: delivery.ConversationID}
	if target != nil {
		query.AroundTs = target.MessageTs
	}
	messages, err := client.History(ctx, query)
	if err != nil {
		return DeliveryAck{}, err
	}
	operation := payload.Operation
	if operation == "" {
		operation = "post_message"
	}
	for _, message := range messages {
		matched := target != nil && message.Ts == target.MessageTs
		if !matched && payload.ClientMessageID != "" && message.ClientMsgID == payload.ClientMessageID {
			matched = true
		}
		if !matched && payload.FallbackDispatchRef != "" && message.ClientMsgID == payload.FallbackDispatchRef {
			matched = true
		}
		if !matched || message.Ts == "" {
			continue
		}
		if operation == "chat_update" && payload.Text != "" && message.Text != payload.Text {
			return retryAck(delivery, "provider_mutation_absent"), nil
		}
		if isKnownDeliveryOperation(operation) {
			return DeliveredAck(delivery, &MessageIdentity{ConversationID: delivery.ConversationID, MessageTs: message.Ts}), nil
		}
		return DeliveredAck(delivery, nil), nil
	}
	if operation == "chat_update" && payload.FallbackText != "" && payload.FallbackDispatchRef != "" {
		return postFallback(ctx, client, delivery, payload, "provider_mutation_absent", ensureCurrent)
	}
	return retryAck(delivery, "provider_mutation_absent"), nil
}

func threadTsOf(delivery *Delivery) string {
	if delivery.ThreadTs == nil {
		return ""
	}
	return *delivery.ThreadTs
}
