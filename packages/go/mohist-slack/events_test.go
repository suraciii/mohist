package mohistslack

import (
	"encoding/json"
	"testing"
)

func mustNormalize(t *testing.T, body any) Envelope {
	t.Helper()
	envelope, err := NormalizeSocketEvent(body)
	if err != nil {
		t.Fatalf("NormalizeSocketEvent() error = %v", err)
	}
	return envelope
}

func TestNormalizeSocketEventRequiresIdentityQuadruple(t *testing.T) {
	base := messageBody("D1", "1700.1", "hello")
	for _, missing := range []string{"api_app_id", "team_id", "channel", "ts"} {
		body := cloneMap(base)
		delete(body, missing)
		if _, err := NormalizeSocketEvent(body); err == nil {
			t.Fatalf("missing %s was accepted", missing)
		}
	}
	// event_ts backs ts up.
	body := cloneMap(base)
	delete(body, "ts")
	body["event_ts"] = "1700.2"
	if envelope := mustNormalize(t, body); envelope.MessageTs != "1700.2" {
		t.Fatalf("event_ts fallback ignored: %+v", envelope)
	}
}

func TestNormalizeSocketEventClassifiesSendersAndDirectMessages(t *testing.T) {
	human := mustNormalize(t, messageBody("D1", "1700.1", "hi"))
	if !human.IsDirectMessage {
		t.Fatal("D-prefixed channel not detected as direct message")
	}
	if human.SenderKind != SenderHuman || human.SenderSlackUserID == nil || *human.SenderSlackUserID != "U1" {
		t.Fatalf("human sender misclassified: %+v", human)
	}
	if human.AuthorBot != nil {
		t.Fatalf("human envelope carried bot author: %+v", human.AuthorBot)
	}

	bot := map[string]any{
		"type": "message", "api_app_id": "A1", "team_id": "T1",
		"channel": "C1", "ts": "1700.3", "bot_id": "B1", "text": "beep",
	}
	botEnvelope := mustNormalize(t, bot)
	if botEnvelope.SenderKind != SenderBot || botEnvelope.SenderSlackUserID != nil {
		t.Fatalf("bot sender misclassified: %+v", botEnvelope)
	}
	if botEnvelope.AuthorBot == nil || botEnvelope.AuthorBot.BotID == nil || *botEnvelope.AuthorBot.BotID != "B1" {
		t.Fatalf("bot author metadata missing: %+v", botEnvelope.AuthorBot)
	}

	unknown := map[string]any{
		"type": "message", "api_app_id": "A1", "team_id": "T1",
		"channel": "C1", "ts": "1700.4", "text": "?",
	}
	if got := mustNormalize(t, unknown).SenderKind; got != SenderUnknown {
		t.Fatalf("unknown sender classified as %s", got)
	}

	imChannel := map[string]any{
		"type": "message", "api_app_id": "A1", "team_id": "T1",
		"channel": "C1", "channel_type": "im", "ts": "1700.5",
	}
	if !mustNormalize(t, imChannel).IsDirectMessage {
		t.Fatal("channel_type im not detected as direct message")
	}
}

func TestNormalizeSocketEventDetectsIdentityConflict(t *testing.T) {
	body := map[string]any{
		"type": "message", "api_app_id": "A1", "team_id": "T1",
		"channel": "C1", "ts": "1700.6", "bot_id": "B1", "user": "U7",
		"bot_profile": map[string]any{"id": "B2", "app_id": "A9"},
	}
	envelope := mustNormalize(t, body)
	if envelope.AuthorBot == nil || !envelope.AuthorBot.IdentityConflict {
		t.Fatalf("identity conflict not recorded: %+v", envelope.AuthorBot)
	}
}

func TestNormalizeSocketEventParsesMentionsAndFiles(t *testing.T) {
	body := map[string]any{
		"type": "message", "api_app_id": "A1", "team_id": "T1",
		"channel": "C1", "ts": "1700.7", "user": "U1",
		"text": "<@U1|dude> and <@U2> again <@U1> plus <@broken text",
		"files": []any{
			map[string]any{"id": "F1", "name": "a.txt", "mimetype": "text/plain", "size": 3.0},
			map[string]any{"id": "F2"}, // incomplete → dropped
			map[string]any{"id": "F3", "name": "b", "mimetype": "x", "size": -1}, // unsafe size → dropped
		},
	}
	envelope := mustNormalize(t, body)
	if len(envelope.MentionedUserIDs) != 2 || envelope.MentionedUserIDs[0] != "U1" || envelope.MentionedUserIDs[1] != "U2" {
		t.Fatalf("mentions = %v, want deduplicated [U1 U2]", envelope.MentionedUserIDs)
	}
	if len(envelope.Files) != 1 || envelope.Files[0].ID != "F1" {
		t.Fatalf("files = %+v, want only the complete one", envelope.Files)
	}
}

func TestNormalizeSocketEventUnwrapsEventContainer(t *testing.T) {
	wrapped := map[string]any{
		"api_app_id": "A1",
		"team_id":    "T1",
		"event":      messageBody("C1", "1700.8", "nested"),
	}
	envelope := mustNormalize(t, wrapped)
	if envelope.ConversationID != "C1" || envelope.Text == nil || *envelope.Text != "nested" {
		t.Fatalf("wrapped event normalized wrong: %+v", envelope)
	}
}

func interactionBody() map[string]any {
	return map[string]any{
		"type":       "block_actions",
		"api_app_id": "A1",
		"team":       map[string]any{"id": "T1"},
		"user":       map[string]any{"id": "U9"},
		"container":  map[string]any{"channel_id": "C1", "message_ts": "1700.9", "thread_ts": "1700.8"},
		"trigger_id": "trig-1",
		"actions": []any{map[string]any{
			"action_id": "act-1",
			"value":     "go",
		}},
	}
}

func TestNormalizeSlackInteractionForwardsSelectionActionID(t *testing.T) {
	body := cloneMap(interactionBody())
	body["actions"].([]any)[0].(map[string]any)["action_id"] = "mohist_select_agent"
	envelope, err := NormalizeSlackInteraction(body)
	if err != nil {
		t.Fatalf("selection interaction rejected: %v", err)
	}
	if envelope.ActionID != "mohist_select_agent" {
		t.Fatalf("selection action id = %q", envelope.ActionID)
	}
}

func TestNormalizeSlackInteractionAcceptsCompletePayload(t *testing.T) {
	envelope, err := NormalizeSlackInteraction(interactionBody())
	if err != nil {
		t.Fatalf("NormalizeSlackInteraction() error = %v", err)
	}
	if envelope.InteractionID != "trig-1" || envelope.ActorSlackUserID != "U9" ||
		envelope.ActionID != "act-1" || envelope.ActionValue != "go" ||
		envelope.ThreadTs == nil || *envelope.ThreadTs != "1700.8" {
		t.Fatalf("interaction normalized wrong: %+v", envelope)
	}
}

func TestNormalizeSlackInteractionRequiresEveryField(t *testing.T) {
	for _, missing := range []string{"api_app_id", "trigger_id", "actions"} {
		body := cloneMap(interactionBody())
		delete(body, missing)
		if _, err := NormalizeSlackInteraction(body); err == nil {
			t.Fatalf("interaction missing %s was accepted", missing)
		}
	}
}

func TestNormalizeSlackInteractionUnwrapsStringInteractivePayload(t *testing.T) {
	inner, _ := json.Marshal(interactionBody())
	wrapped := map[string]any{
		"type":    "interactive",
		"payload": string(inner),
	}
	envelope, err := NormalizeSlackInteraction(wrapped)
	if err != nil {
		t.Fatalf("string-wrapped interactive payload rejected: %v", err)
	}
	if envelope.ActionValue != "go" {
		t.Fatalf("unwrapped payload wrong: %+v", envelope)
	}
}

func TestIsSlackInteractionAndEventType(t *testing.T) {
	if !IsSlackInteraction(interactionBody()) {
		t.Fatal("bare block_actions not recognized")
	}
	if IsSlackInteraction(messageBody("C1", "1700.1", "nope")) {
		t.Fatal("a message was treated as an interaction")
	}
	if got := SlackEventType(messageBody("C1", "1700.1", "x")); got != "message" {
		t.Fatalf("message event type = %q", got)
	}
	if got := SlackEventType(interactionBody()); got != "block_actions" {
		t.Fatalf("interaction event type = %q", got)
	}
}

func cloneMap(source map[string]any) map[string]any {
	clone := make(map[string]any, len(source))
	for key, value := range source {
		clone[key] = value
	}
	return clone
}
