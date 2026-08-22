package mohistslack

import (
	"encoding/json"
	"fmt"
	"regexp"
	"strings"
)

// Normalization ports packages/mohist-slack/src/adapter-events.ts verbatim:
// message envelopes need the stable identity quadruple, interactions are
// block_actions at the top level or wrapped in an interactive payload.

func isRecord(value any) (map[string]any, bool) {
	record, ok := value.(map[string]any)
	return record, ok
}

func recordValue(record map[string]any, key string) any {
	if record == nil {
		return nil
	}
	return record[key]
}

func plainString(value any) string {
	text, ok := value.(string)
	if !ok || text == "" {
		return ""
	}
	return text
}

func fieldString(record map[string]any, key string) string {
	if record == nil {
		return ""
	}
	return plainString(record[key])
}

func nestedRecord(record map[string]any, key string) (map[string]any, bool) {
	if record == nil {
		return nil, false
	}
	return isRecord(record[key])
}

// ConnectionTargetKey and ManagerTargetKey rebuild runtime keys for logging.
// The Target interface's Key method covers both; these helpers exist for
// symmetry with the Node implementation's connectionKey.
func targetKey(target Target) string { return target.Key() }

// NormalizeSocketEvent converts one Socket Mode message body into the wire
// envelope posted to the Server. It fails when the stable identity quadruple
// (api_app_id, team_id, channel, ts/event_ts) is incomplete; such events are
// never acknowledged so Slack redelivers them once identity is present.
func NormalizeSocketEvent(body any) (Envelope, error) {
	bodyRecord, _ := isRecord(body)
	eventRecord, hasEvent := nestedRecord(bodyRecord, "event")
	if !hasEvent {
		eventRecord = bodyRecord
	}
	if eventRecord == nil {
		return Envelope{}, fmt.Errorf("Slack Socket Mode event is malformed")
	}
	apiAppID := firstNonEmpty(fieldString(eventRecord, "api_app_id"), fieldString(bodyRecord, "api_app_id"))
	teamID := firstNonEmpty(fieldString(eventRecord, "team_id"), fieldString(bodyRecord, "team_id"))
	conversationID := fieldString(eventRecord, "channel")
	messageTs := firstNonEmpty(fieldString(eventRecord, "ts"), fieldString(eventRecord, "event_ts"))
	if apiAppID == "" || teamID == "" || conversationID == "" || messageTs == "" {
		return Envelope{}, fmt.Errorf("Slack event is missing its stable identity")
	}
	senderKind := normalizeSenderKind(eventRecord)
	var senderUserID *string
	if senderKind == SenderHuman {
		id := fieldString(eventRecord, "user")
		if id != "" {
			senderUserID = &id
		}
	}
	var authorBot *BotAuthor
	if senderKind == SenderBot {
		authorBot = normalizeBotAuthor(eventRecord)
	}
	envelope := Envelope{
		EventType:         firstNonEmpty(fieldString(eventRecord, "type"), "message"),
		APIAppID:          apiAppID,
		IsDirectMessage:   fieldString(eventRecord, "channel_type") == "im" || strings.HasPrefix(conversationID, "D"),
		TeamID:            teamID,
		ConversationID:    conversationID,
		MessageTs:         messageTs,
		MentionedUserIDs:  parseMentionedUserIds(textOf(eventRecord)),
		SenderSlackUserID: senderUserID,
		SenderKind:        senderKind,
		AuthorBot:         authorBot,
		Text:              textPointer(eventRecord),
		Files:             parseFiles(eventRecord["files"]),
	}
	if threadTs := fieldString(eventRecord, "thread_ts"); threadTs != "" {
		envelope.ThreadTs = &threadTs
	}
	return envelope, nil
}

// NormalizeSlackInteraction converts one block_actions body into the wire
// interaction envelope. It fails when any required identity field is absent.
func NormalizeSlackInteraction(body any) (InteractionEnvelope, error) {
	payload, ok := interactionPayload(body)
	if !ok || !isBlockActions(payload) {
		return InteractionEnvelope{}, fmt.Errorf("Slack interaction is malformed")
	}
	apiAppID := fieldString(payload, "api_app_id")
	team := ""
	if teamRecord, ok := nestedRecord(payload, "team"); ok {
		team = fieldString(teamRecord, "id")
	} else {
		team = fieldString(payload, "team_id")
	}
	user := ""
	if userRecord, ok := nestedRecord(payload, "user"); ok {
		user = fieldString(userRecord, "id")
	}
	container, hasContainer := nestedRecord(payload, "container")
	conversationID := ""
	messageTs := ""
	threadTs := ""
	if hasContainer {
		conversationID = fieldString(container, "channel_id")
		messageTs = fieldString(container, "message_ts")
		threadTs = fieldString(container, "thread_ts")
	}
	action := firstAction(payload)
	interactionID := firstNonEmpty(
		fieldString(payload, "trigger_id"),
		fieldString(action, "action_ts"),
		fieldString(payload, "event_id"),
	)
	actionID := fieldString(action, "action_id")
	actionValue := fieldString(action, "value")
	if apiAppID == "" || team == "" || user == "" || conversationID == "" || messageTs == "" ||
		interactionID == "" || actionID == "" || actionValue == "" {
		return InteractionEnvelope{}, fmt.Errorf("Slack interaction is missing its stable identity")
	}
	envelope := InteractionEnvelope{
		EventType:        "block_actions",
		APIAppID:         apiAppID,
		InteractionID:    interactionID,
		TeamID:           team,
		ConversationID:   conversationID,
		MessageTs:        messageTs,
		ActorSlackUserID: user,
		ActionID:         actionID,
		ActionValue:      actionValue,
	}
	if threadTs != "" {
		envelope.ThreadTs = &threadTs
	}
	return envelope, nil
}

// IsSlackInteraction reports whether the body is a block_actions payload,
// either bare or wrapped in an interactive container.
func IsSlackInteraction(body any) bool {
	payload, ok := interactionPayload(body)
	return ok && isBlockActions(payload)
}

// SlackEventType names a socket body for logs: the interaction type when the
// body is interactive, else the event type.
func SlackEventType(body any) string {
	if payload, ok := interactionPayload(body); ok {
		if name := fieldString(payload, "type"); name != "" {
			return name
		}
		return "interactive"
	}
	bodyRecord, _ := isRecord(body)
	eventRecord, hasEvent := nestedRecord(bodyRecord, "event")
	if !hasEvent {
		eventRecord = bodyRecord
	}
	if name := fieldString(eventRecord, "type"); name != "" {
		return name
	}
	return "unknown"
}

func isBlockActions(payload map[string]any) bool {
	return fieldString(payload, "type") == "block_actions"
}

func firstAction(payload map[string]any) map[string]any {
	actions, ok := payload["actions"].([]any)
	if !ok || len(actions) == 0 {
		return nil
	}
	record, ok := actions[0].(map[string]any)
	if !ok {
		return nil
	}
	return record
}

func interactionPayload(body any) (map[string]any, bool) {
	record, ok := isRecord(body)
	if !ok {
		return nil, false
	}
	if isBlockActions(record) {
		return record, true
	}
	if fieldString(record, "type") != "interactive" {
		return nil, false
	}
	switch payload := record["payload"].(type) {
	case map[string]any:
		return payload, true
	case string:
		var parsed any
		if json.Unmarshal([]byte(payload), &parsed) != nil {
			return nil, false
		}
		return isRecord(parsed)
	default:
		return nil, false
	}
}

func parseFiles(value any) []FileRef {
	list, ok := value.([]any)
	if !ok {
		return nil
	}
	files := make([]FileRef, 0, len(list))
	for _, candidate := range list {
		record, ok := isRecord(candidate)
		if !ok {
			continue
		}
		id := fieldString(record, "id")
		name := fieldString(record, "name")
		mimetype := fieldString(record, "mimetype")
		size, ok := record["size"].(float64)
		safeSize := ok && size >= 0 && size == float64(int64(size))
		if id == "" || name == "" || mimetype == "" || !safeSize {
			continue
		}
		files = append(files, FileRef{ID: id, Name: name, Mimetype: mimetype, Size: int64(size)})
	}
	return files
}

func normalizeSenderKind(event map[string]any) SenderKind {
	if fieldString(event, "bot_id") != "" || fieldString(event, "subtype") == "bot_message" {
		return SenderBot
	}
	if _, ok := nestedRecord(event, "bot_profile"); ok {
		return SenderBot
	}
	if fieldString(event, "user") != "" {
		return SenderHuman
	}
	return SenderUnknown
}

func normalizeBotAuthor(event map[string]any) *BotAuthor {
	botProfile, hasProfile := nestedRecord(event, "bot_profile")
	eventAppID := fieldString(event, "app_id")
	profileAppID := ""
	if hasProfile {
		profileAppID = fieldString(botProfile, "app_id")
	}
	eventBotID := fieldString(event, "bot_id")
	profileBotID := ""
	if hasProfile {
		profileBotID = fieldString(botProfile, "id")
	}
	appID := firstNonEmpty(eventAppID, profileAppID)
	botID := firstNonEmpty(eventBotID, profileBotID)
	identityConflict :=
		(eventAppID != "" && profileAppID != "" && eventAppID != profileAppID) ||
			(eventBotID != "" && profileBotID != "" && eventBotID != profileBotID)
	botUserID := fieldString(event, "user")
	if appID == "" && botID == "" && botUserID == "" && !identityConflict {
		return nil
	}
	author := &BotAuthor{IdentityConflict: identityConflict}
	if appID != "" {
		author.AppID = &appID
	}
	if botID != "" {
		author.BotID = &botID
	}
	if botUserID != "" {
		author.BotUserID = &botUserID
	}
	return author
}

var mentionPattern = regexp.MustCompile(`<@([A-Za-z0-9_-]+)(?:\|[^>]*)?>`)

func parseMentionedUserIds(text string) []string {
	if text == "" {
		return nil
	}
	seen := map[string]bool{}
	mentions := []string{}
	for _, match := range mentionPattern.FindAllStringSubmatch(text, -1) {
		userID := match[1]
		if userID == "" || seen[userID] {
			continue
		}
		seen[userID] = true
		mentions = append(mentions, userID)
	}
	return mentions
}

func textOf(event map[string]any) string {
	if text, ok := event["text"].(string); ok {
		return text
	}
	return ""
}

func textPointer(event map[string]any) *string {
	if text, ok := event["text"].(string); ok {
		return &text
	}
	return nil
}

func firstNonEmpty(values ...string) string {
	for _, value := range values {
		if value != "" {
			return value
		}
	}
	return ""
}
