package mohistslack

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
)

// Delivery settlement ports packages/mohist-slack/src/adapter-delivery.ts.
// Outcomes are delivered (optionally with the provider message identity),
// retry (with reason), and uncertain (with reason). ensureCurrent panics
// with StaleRuntimeError when the runtime was superseded; adapter.go recovers
// it at the drain boundary, mirroring the Node throw across await points.

// Reaction operations.
const (
	OpPostMessage    = "post_message"
	OpChatUpdate     = "chat_update"
	OpReactionAdd    = "reaction_add"
	OpReactionRemove = "reaction_remove"
	OpUploadFile     = "upload_file"
)

const (
	providerMutationAbsent    = "provider_mutation_absent"
	providerHistoryIncomplete = "provider_history_incomplete"
	// Keep provider lookups bounded so a busy conversation cannot monopolize an adapter worker.
	historyPageBudget = 8

	// Reserve a delimiter outside normal producer ids to keep base and segment namespaces disjoint.
	segmentDispatchSeparator = "\x1f"
)

var errHistorySearchIncomplete = errors.New("conversation history search incomplete")

// DeliveryPayload is one claimed delivery's decoded payload. Unknown fields
// are ignored; required-field validation happens where the Node
// implementation raises its errors.
type DeliveryPayload struct {
	Operation               string           `json:"operation,omitempty"`
	Text                    string           `json:"-"`
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

	// textPresent distinguishes an explicit empty text from an absent field:
	// the Node post_message path coalesces with ??, not truthiness.
	textPresent bool
}

// ParseDeliveryPayload decodes one payload JSON object.
func ParseDeliveryPayload(value string) (DeliveryPayload, error) {
	var probe any
	if err := json.Unmarshal([]byte(value), &probe); err != nil {
		return DeliveryPayload{}, fmt.Errorf("delivery payload was not valid JSON")
	}
	if _, ok := probe.(map[string]any); !ok {
		return DeliveryPayload{}, errors.New("Delivery payload was not an object")
	}
	var wire struct {
		DeliveryPayload
		Text *string `json:"text"`
	}
	var payload DeliveryPayload
	if err := json.Unmarshal([]byte(value), &wire); err != nil {
		return DeliveryPayload{}, fmt.Errorf("delivery payload was not a known shape")
	}
	payload = wire.DeliveryPayload
	if wire.Text != nil {
		payload.Text = *wire.Text
		payload.textPresent = true
	}
	return payload, nil
}

func requiredText(payload DeliveryPayload) (string, error) {
	if payload.Text == "" {
		return "", errors.New("Delivery payload did not contain text")
	}
	return payload.Text, nil
}

// DeliveredAck builds a delivered outcome, optionally with identity.
func DeliveredAck(delivery *Delivery, identity *MessageIdentity) DeliveryAck {
	ack := DeliveryAck{ID: delivery.ID, Outcome: OutcomeDelivered}
	if identity != nil {
		ack.ProviderMessageIdentity = identity
	}
	return ack
}

// WithAdapterID stamps the adapter identity onto an ack.
func WithAdapterID(ack DeliveryAck, adapterID string) DeliveryAck {
	ack.AdapterID = adapterID
	return ack
}

// IsKnownDeliveryOperation reports whether the payload names a primary-path
// operation; unknown operations route to reconciliation.
func IsKnownDeliveryOperation(operation string) bool {
	switch operation {
	case OpPostMessage, OpChatUpdate, OpReactionAdd, OpReactionRemove, OpUploadFile:
		return true
	default:
		return false
	}
}

func retryAck(delivery *Delivery, reason string) DeliveryAck {
	return DeliveryAck{ID: delivery.ID, Outcome: OutcomeRetry, Reason: reason}
}

func uncertainAck(delivery *Delivery, reason string) DeliveryAck {
	return DeliveryAck{ID: delivery.ID, Outcome: OutcomeUncertain, Reason: reason}
}

func rejectionReason(err error, fallback string) string {
	if code := SlackErrorCode(err); code != "" {
		return code
	}
	if err != nil && err.Error() != "" {
		return err.Error()
	}
	return fallback
}

// mutateReaction performs reactions.add/remove and treats an already-reached
// provider state as success. Other coded rejections become (code, false);
// uncoded failures return as transport errors.
func mutateReaction(ctx context.Context, web WebClient, operation string, target MessageIdentity, reaction string, ensureCurrent func()) (string, bool, error) {
	ensureCurrent()
	var err error
	if operation == OpReactionAdd {
		err = web.AddReaction(ctx, target.ConversationID, reaction, target.MessageTs)
	} else {
		err = web.RemoveReaction(ctx, target.ConversationID, reaction, target.MessageTs)
	}
	ensureCurrent()
	if err == nil {
		return "", true, nil
	}
	if code := SlackErrorCode(err); code != "" {
		if (operation == OpReactionAdd && code == "already_reacted") ||
			(operation == OpReactionRemove && code == "no_reaction") {
			return "", true, nil
		}
		return code, false, nil
	}
	return "", false, err
}

// getReactions fetches the reaction names on one message with the same coded
// rejection normalization as mutateReaction.
func getReactions(ctx context.Context, web WebClient, target MessageIdentity, ensureCurrent func()) ([]string, string, error) {
	ensureCurrent()
	names, err := web.GetReactions(ctx, target.ConversationID, target.MessageTs)
	ensureCurrent()
	if err == nil {
		return names, "", nil
	}
	if code := SlackErrorCode(err); code != "" {
		return nil, code, nil
	}
	return nil, "", err
}

// FindStatusMessage looks up the status message for a dispatch ref by
// client_msg_id in recent conversation history. A missing web client yields
// no status target, mirroring the Node optional chain.
func FindStatusMessage(ctx context.Context, web WebClient, conversationID, clientMessageID string, ensureCurrent func()) (*MessageIdentity, error) {
	if web == nil {
		return nil, nil
	}
	history, complete, err := readConversationHistory(ctx, web, HistoryInput{Channel: conversationID, Limit: 200}, ensureCurrent)
	if err != nil {
		return nil, err
	}
	for _, candidate := range history {
		if candidate.ClientMsgID == clientMessageID && candidate.TS != "" {
			return &MessageIdentity{ConversationID: conversationID, MessageTs: candidate.TS}, nil
		}
	}
	if !complete {
		return nil, errHistorySearchIncomplete
	}
	return nil, nil
}

func readConversationHistory(ctx context.Context, web WebClient, input HistoryInput, ensureCurrent func()) ([]HistoryMessage, bool, error) {
	history := make([]HistoryMessage, 0, input.Limit)
	for pageNumber := 0; pageNumber < historyPageBudget; pageNumber++ {
		page, err := GetConversationHistory(ctx, web, input, ensureCurrent)
		if err != nil {
			return nil, false, err
		}
		history = append(history, page.Messages...)
		if !page.HasMore {
			return history, true, nil
		}
		if page.NextCursor == "" || page.NextCursor == input.Cursor {
			return history, false, nil
		}
		input.Cursor = page.NextCursor
	}
	return history, false, nil
}

// GetConversationHistory invokes conversations.history through the seam,
// converting typed rejections into coded results the caller can inspect via
// SlackErrorCode on the returned error.
func GetConversationHistory(ctx context.Context, web WebClient, input HistoryInput, ensureCurrent func()) (HistoryPage, error) {
	ensureCurrent()
	page, err := web.GetConversationHistory(ctx, input)
	ensureCurrent()
	if err != nil {
		if code := SlackErrorCode(err); code != "" {
			err = &SlackError{Code: code}
		}
		return HistoryPage{}, err
	}
	return page, nil
}

// MutateDelivery performs one delivery's primary path or degradation ladder.
func MutateDelivery(ctx context.Context, web WebClient, delivery *Delivery, ensureCurrent func()) (DeliveryAck, error) {
	ensureCurrent()
	payload, err := ParseDeliveryPayload(delivery.PayloadJSON)
	if err != nil {
		return DeliveryAck{}, err
	}
	operation := payload.Operation
	if operation == "" {
		operation = OpPostMessage
	}
	if !IsKnownDeliveryOperation(operation) {
		return Reconcile(ctx, web, delivery, ensureCurrent)
	}
	if len(payload.Segments) > 1 {
		return deliverSegments(ctx, web, delivery, payload, ensureCurrent)
	}

	switch operation {
	case OpChatUpdate:
		target := payload.ProviderMessageIdentity
		if target == nil {
			return DeliveryAck{}, errors.New("chat.update delivery has no provider message identity")
		}
		text, err := requiredText(payload)
		if err != nil {
			return DeliveryAck{}, err
		}
		ensureCurrent()
		ts, err := web.UpdateMessage(ctx, UpdateMessageInput{
			Channel: target.ConversationID,
			TS:      target.MessageTs,
			Text:    text,
			Blocks:  payload.Blocks,
		})
		ensureCurrent()
		if err != nil {
			code := SlackErrorCode(err)
			if code == "" {
				return DeliveryAck{}, err
			}
			return fallbackAfterUpdateFailure(ctx, web, delivery, payload, code, ensureCurrent)
		}
		identityTS := ts
		if identityTS == "" {
			identityTS = target.MessageTs
		}
		return DeliveredAck(delivery, &MessageIdentity{ConversationID: target.ConversationID, MessageTs: identityTS}), nil

	case OpReactionAdd, OpReactionRemove:
		target := payload.TargetMessageIdentity
		if target == nil || payload.Reaction == "" {
			return DeliveryAck{}, fmt.Errorf("%s delivery is missing its target", operation)
		}
		code, ok, err := mutateReaction(ctx, web, operation, *target, payload.Reaction, ensureCurrent)
		if err != nil {
			return DeliveryAck{}, err
		}
		if ok {
			return DeliveredAck(delivery, nil), nil
		}
		if !IsUnsupportedReactionError(code) {
			return retryAck(delivery, code), nil
		}
		if code == "missing_scope" {
			if payload.FallbackText == "" || fallbackDispatchRef(payload) == "" {
				return DeliveredAck(delivery, nil), nil
			}
			return postFallback(ctx, web, delivery, payload, code, ensureCurrent)
		}
		statusTarget, err := findStatusTarget(ctx, web, delivery, payload, ensureCurrent)
		if err != nil {
			if errors.Is(err, errHistorySearchIncomplete) {
				return uncertainAck(delivery, providerHistoryIncomplete), nil
			}
			return DeliveryAck{}, err
		}
		if statusTarget != nil && statusTarget.MessageTs != target.MessageTs {
			retargetCode, retargetOK, err := mutateReaction(ctx, web, operation, *statusTarget, payload.Reaction, ensureCurrent)
			if err != nil {
				return DeliveryAck{}, err
			}
			if retargetOK {
				return DeliveredAck(delivery, nil), nil
			}
			if !IsUnsupportedReactionError(retargetCode) {
				return retryAck(delivery, retargetCode), nil
			}
			code = retargetCode
		}
		if operation == OpReactionRemove {
			return DeliveredAck(delivery, nil), nil
		}
		if payload.FallbackText != "" && fallbackDispatchRef(payload) != "" {
			return postFallback(ctx, web, delivery, payload, code, ensureCurrent)
		}
		return DeliveredAck(delivery, nil), nil

	case OpUploadFile:
		return uploadFile(ctx, web, delivery, payload, ensureCurrent)
	}

	// post_message: update the status message when one exists, else post.
	// The Node path coalesces with ??: an explicit empty text stays empty
	// when blocks exist; only an absent text requires one.
	var text string
	if payload.textPresent {
		text = payload.Text
	} else if len(payload.Blocks) > 0 {
		text = ""
	} else {
		text, err = requiredText(payload)
		if err != nil {
			return DeliveryAck{}, err
		}
	}
	existingStatus, err := findStatusTarget(ctx, web, delivery, payload, ensureCurrent)
	if err != nil {
		if errors.Is(err, errHistorySearchIncomplete) {
			return uncertainAck(delivery, providerHistoryIncomplete), nil
		}
		return DeliveryAck{}, err
	}
	if existingStatus != nil {
		ensureCurrent()
		ts, err := web.UpdateMessage(ctx, UpdateMessageInput{
			Channel: existingStatus.ConversationID,
			TS:      existingStatus.MessageTs,
			Text:    text,
			Blocks:  payload.Blocks,
		})
		ensureCurrent()
		if err != nil {
			if code := SlackErrorCode(err); code != "" {
				return retryAck(delivery, code), nil
			}
			return DeliveryAck{}, err
		}
		identityTS := ts
		if identityTS == "" {
			identityTS = existingStatus.MessageTs
		}
		return DeliveredAck(delivery, &MessageIdentity{ConversationID: existingStatus.ConversationID, MessageTs: identityTS}), nil
	}
	ensureCurrent()
	ts, err := web.PostMessage(ctx, PostMessageInput{
		Channel:     delivery.ConversationID,
		Text:        text,
		ThreadTs:    derefString(delivery.ThreadTs),
		ClientMsgID: payload.ClientMessageID,
		Blocks:      payload.Blocks,
	})
	ensureCurrent()
	if err != nil {
		if code := SlackErrorCode(err); code != "" {
			return retryAck(delivery, code), nil
		}
		return DeliveryAck{}, err
	}
	var identity *MessageIdentity
	if ts != "" {
		identity = &MessageIdentity{ConversationID: delivery.ConversationID, MessageTs: ts}
	}
	return DeliveredAck(delivery, identity), nil
}

func deliverSegments(ctx context.Context, web WebClient, delivery *Delivery, payload DeliveryPayload, ensureCurrent func()) (DeliveryAck, error) {
	refs, err := segmentDispatchRefs(payload)
	if err != nil {
		return DeliveryAck{}, err
	}
	if len(refs) != len(payload.Segments) {
		return DeliveryAck{}, errors.New("segmented delivery is missing a client message id")
	}
	threadTs := derefString(delivery.ThreadTs)
	var firstIdentity *MessageIdentity
	for index, segment := range payload.Segments {
		input := PostMessageInput{
			Channel:     delivery.ConversationID,
			Text:        segment,
			ThreadTs:    threadTs,
			ClientMsgID: refs[index],
		}
		ensureCurrent()
		ts, err := web.PostMessage(ctx, input)
		ensureCurrent()
		if err != nil {
			if code := SlackErrorCode(err); code != "" {
				return retryAck(delivery, code), nil
			}
			return DeliveryAck{}, err
		}
		if index == 0 && ts != "" {
			firstIdentity = &MessageIdentity{ConversationID: delivery.ConversationID, MessageTs: ts}
		}
	}
	return DeliveredAck(delivery, firstIdentity), nil
}

func uploadFile(ctx context.Context, web WebClient, delivery *Delivery, payload DeliveryPayload, ensureCurrent func()) (DeliveryAck, error) {
	if payload.FileName == "" || payload.FileContentBase64 == "" {
		return DeliveryAck{}, errors.New("upload_file delivery is missing the Slack upload client or file payload")
	}
	content, err := base64Decode(payload.FileContentBase64)
	if err != nil {
		return DeliveryAck{}, errors.New("upload_file delivery carried an invalid base64 payload")
	}
	ensureCurrent()
	result, err := web.UploadFileV2(ctx, FileUploadInput{
		ChannelID:      delivery.ConversationID,
		ThreadTs:       derefString(delivery.ThreadTs),
		Filename:       payload.FileName,
		Content:        content,
		InitialComment: payload.Text,
	})
	ensureCurrent()
	if err != nil {
		if code := SlackErrorCode(err); code != "" {
			return retryAck(delivery, code), nil
		}
		return DeliveryAck{}, err
	}
	identity := fileShareIdentity(delivery, result)
	if identity == nil && result.FileID != "" {
		page, histErr := GetConversationHistory(ctx, web, HistoryInput{Channel: delivery.ConversationID, Limit: 200}, ensureCurrent)
		if histErr == nil {
			for _, candidate := range page.Messages {
				if candidate.TS == "" {
					continue
				}
				for _, fileID := range candidate.FileIDs {
					if fileID == result.FileID {
						identity = &MessageIdentity{ConversationID: delivery.ConversationID, MessageTs: candidate.TS}
						break
					}
				}
				if identity != nil {
					break
				}
			}
		} else if SlackErrorCode(histErr) == "" {
			return DeliveryAck{}, histErr
		}
	}
	return DeliveredAck(delivery, identity), nil
}

func fileShareIdentity(delivery *Delivery, result FileUploadResult) *MessageIdentity {
	ts := result.PublicShareTS
	if ts == "" {
		ts = result.PrivateShareTS
	}
	if ts == "" {
		return nil
	}
	return &MessageIdentity{ConversationID: delivery.ConversationID, MessageTs: ts}
}

func fallbackAfterUpdateFailure(ctx context.Context, web WebClient, delivery *Delivery, payload DeliveryPayload, reason string, ensureCurrent func()) (DeliveryAck, error) {
	if payload.FallbackText == "" || fallbackDispatchRef(payload) == "" {
		return retryAck(delivery, reason), nil
	}
	return postFallback(ctx, web, delivery, payload, reason, ensureCurrent)
}

func postFallback(ctx context.Context, web WebClient, delivery *Delivery, payload DeliveryPayload, reason string, ensureCurrent func()) (DeliveryAck, error) {
	fallbackText := payload.FallbackText
	if fallbackText == "" {
		fallbackText = payload.Text
	}
	fallbackRef := fallbackDispatchRef(payload)
	existing, err := FindStatusMessage(ctx, web, delivery.ConversationID, fallbackRef, ensureCurrent)
	if err != nil {
		if errors.Is(err, errHistorySearchIncomplete) {
			return uncertainAck(delivery, providerHistoryIncomplete), nil
		}
		return DeliveryAck{}, err
	}
	if existing != nil {
		return DeliveredAck(delivery, existing), nil
	}
	ensureCurrent()
	ts, err := web.PostMessage(ctx, PostMessageInput{
		Channel:     delivery.ConversationID,
		Text:        fallbackText,
		ThreadTs:    derefString(delivery.ThreadTs),
		ClientMsgID: fallbackRef,
		Blocks:      payload.Blocks,
	})
	ensureCurrent()
	if err != nil {
		if code := SlackErrorCode(err); code != "" {
			return retryAck(delivery, code), nil
		}
		return DeliveryAck{}, err
	}
	var identity *MessageIdentity
	if ts != "" {
		identity = &MessageIdentity{ConversationID: delivery.ConversationID, MessageTs: ts}
	}
	return DeliveredAck(delivery, identity), nil
}

func segmentDispatchRef(base string, index int) (string, error) {
	if strings.Contains(base, segmentDispatchSeparator) {
		return "", fmt.Errorf("client message id contains the reserved segment separator")
	}
	if index == 0 {
		return base, nil
	}
	return fmt.Sprintf("%s%s%d", base, segmentDispatchSeparator, index), nil
}

func segmentDispatchRefs(payload DeliveryPayload) ([]string, error) {
	if len(payload.Segments) <= 1 || payload.ClientMessageID == "" {
		return nil, nil
	}
	refs := make([]string, len(payload.Segments))
	for index := range payload.Segments {
		ref, err := segmentDispatchRef(payload.ClientMessageID, index)
		if err != nil {
			return nil, err
		}
		refs[index] = ref
	}
	return refs, nil
}

func fallbackDispatchRef(payload DeliveryPayload) string {
	if payload.FallbackDispatchRef != "" {
		return payload.FallbackDispatchRef
	}
	if payload.ClientMessageID == "" {
		return ""
	}
	return payload.ClientMessageID + ":fallback"
}

func findStatusTarget(ctx context.Context, web WebClient, delivery *Delivery, payload DeliveryPayload, ensureCurrent func()) (*MessageIdentity, error) {
	if payload.StatusDispatchRef == "" {
		return nil, nil
	}
	return FindStatusMessage(ctx, web, delivery.ConversationID, payload.StatusDispatchRef, ensureCurrent)
}

// Reconcile settles an uncertain or unknown-operation delivery against
// provider state: reactions via reactions.get compared with the intended
// operation, messages via history matched on ts / client_msg_id /
// fallbackDispatchRef.
func Reconcile(ctx context.Context, web WebClient, delivery *Delivery, ensureCurrent func()) (DeliveryAck, error) {
	ensureCurrent()
	payload, err := ParseDeliveryPayload(delivery.PayloadJSON)
	if err != nil {
		return DeliveryAck{}, err
	}
	target := payload.ProviderMessageIdentity
	if target == nil {
		target = payload.TargetMessageIdentity
	}
	isReaction := payload.Operation == OpReactionAdd || payload.Operation == OpReactionRemove
	if isReaction && target != nil && payload.Reaction != "" {
		if web == nil {
			return uncertainAck(delivery, "Slack client cannot reconcile reactions"), nil
		}
		names, code, err := getReactions(ctx, web, *target, ensureCurrent)
		if err != nil {
			return DeliveryAck{}, err
		}
		if code != "" {
			if !IsUnsupportedReactionError(code) {
				return uncertainAck(delivery, code), nil
			}
			if payload.FallbackText == "" || fallbackDispatchRef(payload) == "" {
				return DeliveredAck(delivery, target), nil
			}
			return postFallback(ctx, web, delivery, payload, code, ensureCurrent)
		}
		present := false
		for _, name := range names {
			if name == payload.Reaction {
				present = true
				break
			}
		}
		deliveredState := present
		if payload.Operation == OpReactionRemove {
			deliveredState = !present
		}
		if deliveredState {
			return DeliveredAck(delivery, target), nil
		}
		return retryAck(delivery, providerMutationAbsent), nil
	}

	if web == nil {
		return uncertainAck(delivery, "Slack client cannot reconcile messages"), nil
	}
	segmented := len(payload.Segments) > 1
	segmentRefs, err := segmentDispatchRefs(payload)
	if err != nil {
		return DeliveryAck{}, err
	}
	if segmented && len(segmentRefs) == 0 {
		return retryAck(delivery, providerMutationAbsent), nil
	}
	historyInput := HistoryInput{
		Channel: delivery.ConversationID,
		Latest:  derefIdentityTS(target),
		Oldest:  derefIdentityTS(target),
		Limit:   200,
	}
	if segmented {
		// A segmented post has identities for every message, so it must search
		// the conversation rather than only the original update target.
		historyInput.Latest = ""
		historyInput.Oldest = ""
	}
	history, complete, err := readConversationHistory(ctx, web, historyInput, ensureCurrent)
	if err != nil {
		if code := SlackErrorCode(err); code != "" {
			return uncertainAck(delivery, code), nil
		}
		return DeliveryAck{}, err
	}
	var match *HistoryMessage
	if segmented {
		found := make(map[string]HistoryMessage, len(segmentRefs))
		for _, candidate := range history {
			if candidate.TS == "" {
				continue
			}
			for _, ref := range segmentRefs {
				if candidate.ClientMsgID == ref {
					found[ref] = candidate
					break
				}
			}
		}
		if len(found) != len(segmentRefs) {
			if !complete {
				return uncertainAck(delivery, providerHistoryIncomplete), nil
			}
			return retryAck(delivery, providerMutationAbsent), nil
		}
		first := found[segmentRefs[0]]
		match = &first
	} else {
		fallbackRef := fallbackDispatchRef(payload)
		for i := range history {
			candidate := history[i]
			if (target != nil && candidate.TS == target.MessageTs) ||
				(payload.ClientMessageID != "" && candidate.ClientMsgID == payload.ClientMessageID) ||
				(fallbackRef != "" && candidate.ClientMsgID == fallbackRef) {
				match = &candidate
				break
			}
		}
	}
	if match != nil && match.TS != "" {
		if !segmented && payload.Operation == OpChatUpdate && payload.Text != "" && match.Text != payload.Text {
			return retryAck(delivery, providerMutationAbsent), nil
		}
		if IsKnownDeliveryOperation(orDefault(payload.Operation, OpPostMessage)) {
			return DeliveredAck(delivery, &MessageIdentity{ConversationID: delivery.ConversationID, MessageTs: match.TS}), nil
		}
		return DeliveredAck(delivery, nil), nil
	}
	if !complete {
		return uncertainAck(delivery, providerHistoryIncomplete), nil
	}
	if payload.Operation == OpChatUpdate && payload.FallbackText != "" && fallbackDispatchRef(payload) != "" {
		if web == nil {
			return uncertainAck(delivery, "Slack client cannot post fallback"), nil
		}
		return postFallback(ctx, web, delivery, payload, providerMutationAbsent, ensureCurrent)
	}
	return retryAck(delivery, providerMutationAbsent), nil
}

func base64Decode(value string) ([]byte, error) {
	return base64.StdEncoding.DecodeString(value)
}

func derefString(value *string) string {
	if value == nil {
		return ""
	}
	return *value
}

func derefIdentityTS(identity *MessageIdentity) string {
	if identity == nil {
		return ""
	}
	return identity.MessageTs
}

func orDefault(value, fallback string) string {
	if value == "" {
		return fallback
	}
	return value
}
