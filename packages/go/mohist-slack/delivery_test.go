package mohistslack

import (
	"context"
	"encoding/base64"
	"errors"
	"strings"
	"testing"
)

const testConversation = "D1"

func testDelivery(id, payload string) *Delivery {
	return &Delivery{ID: id, ConversationID: testConversation, PayloadJSON: payload}
}

func mutate(t *testing.T, web WebClient, delivery *Delivery) DeliveryAck {
	t.Helper()
	ack, err := MutateDelivery(context.Background(), web, delivery, func() {})
	if err != nil {
		t.Fatalf("MutateDelivery returned error: %v", err)
	}
	return ack
}

func reconcileNow(t *testing.T, web WebClient, delivery *Delivery) DeliveryAck {
	t.Helper()
	ack, err := Reconcile(context.Background(), web, delivery, func() {})
	if err != nil {
		t.Fatalf("Reconcile returned error: %v", err)
	}
	return ack
}

func TestPostMessageDeliveryPostsAndReportsIdentity(t *testing.T) {
	web := &fakeWeb{postTS: "1701.1"}
	delivery := testDelivery("d-1", `{"text":"hello","clientMessageId":"cmid-1","statusDispatchRef":"status-1"}`)
	web.historyFn = func(HistoryInput) ([]HistoryMessage, error) { return nil, nil }

	ack := mutate(t, web, delivery)

	// The dispatch ref found no prior message, so the primary post ran.
	if len(web.updates) != 0 {
		t.Fatalf("unexpected updates: %+v", web.updates)
	}
	post := web.posts[0]
	if post.Channel != testConversation || post.Text != "hello" || post.ClientMsgID != "cmid-1" {
		t.Fatalf("post input = %+v", post)
	}
	if ack.Outcome != OutcomeDelivered || ack.ProviderMessageIdentity == nil ||
		ack.ProviderMessageIdentity.MessageTs != "1701.1" {
		t.Fatalf("ack = %+v", ack)
	}
}

func TestPostMessageUpdatesExistingStatusMessage(t *testing.T) {
	web := &fakeWeb{updateTS: "1701.2"}
	delivery := testDelivery("d-2", `{"text":"progress","clientMessageId":"cmid-2","statusDispatchRef":"status-2"}`)
	web.historyFn = func(input HistoryInput) ([]HistoryMessage, error) {
		return []HistoryMessage{{TS: "1700.9", ClientMsgID: "status-2", Text: "older"}}, nil
	}

	ack := mutate(t, web, delivery)

	if len(web.posts) != 0 {
		t.Fatalf("unexpected posts: %+v", web.posts)
	}
	update := web.updates[0]
	if update.Channel != testConversation || update.TS != "1700.9" || update.Text != "progress" {
		t.Fatalf("update input = %+v", update)
	}
	if ack.ProviderMessageIdentity.MessageTs != "1701.2" {
		t.Fatalf("ack identity = %+v", ack.ProviderMessageIdentity)
	}
}

func TestPostMessageRejectionRetriesWithCode(t *testing.T) {
	web := &fakeWeb{postErr: &SlackError{Code: "msg_too_long"}}
	ack := mutate(t, web, testDelivery("d-3", `{"text":"x"}`))
	if ack.Outcome != OutcomeRetry || ack.Reason != "msg_too_long" {
		t.Fatalf("ack = %+v", ack)
	}

	web.postErr = errors.New("connection reset")
	if _, err := MutateDelivery(context.Background(), web, testDelivery("d-3b", `{"text":"x"}`), func() {}); err == nil {
		t.Fatal("an uncoded transport failure must propagate")
	}
}

func TestChatUpdateRequiresProviderIdentity(t *testing.T) {
	web := &fakeWeb{}
	if _, err := MutateDelivery(context.Background(),
		web, testDelivery("d-4", `{"operation":"chat_update","text":"x"}`), func() {}); err == nil {
		t.Fatal("chat_update without provider identity was accepted")
	}
}

func TestChatUpdateSuccessDeliversWithTargetIdentity(t *testing.T) {
	web := &fakeWeb{} // update returns empty ts → target ts wins
	payload := `{"operation":"chat_update","text":"new","providerMessageIdentity":{"conversationId":"D1","messageTs":"1699.1"}}`
	ack := mutate(t, web, testDelivery("d-5", payload))
	if ack.Outcome != OutcomeDelivered || ack.ProviderMessageIdentity.MessageTs != "1699.1" {
		t.Fatalf("ack = %+v", ack)
	}
}

func TestChatUpdateFailureFallsBackOrRetries(t *testing.T) {
	base := `"operation":"chat_update","providerMessageIdentity":{"conversationId":"D1","messageTs":"1699.2"}`

	withFallback := &fakeWeb{updateErr: &SlackError{Code: "cant_edit_message"}, postTS: "1701.3"}
	fallbackPayload := `{` + base + `,"text":"new","fallbackText":"original","fallbackDispatchRef":"fb-1"}`
	ack := mutate(t, withFallback, testDelivery("d-6", fallbackPayload))
	if ack.Outcome != OutcomeDelivered {
		t.Fatalf("fallback ack = %+v", ack)
	}
	if post := withFallback.posts[0]; post.ClientMsgID != "fb-1" || post.Text != "original" {
		t.Fatalf("fallback post = %+v", post)
	}

	withoutFallback := &fakeWeb{updateErr: &SlackError{Code: "cant_edit_message"}}
	retryPayload := `{` + base + `,"text":"new"}`
	ack = mutate(t, withoutFallback, testDelivery("d-7", retryPayload))
	if ack.Outcome != OutcomeRetry || ack.Reason != "cant_edit_message" {
		t.Fatalf("retry ack = %+v", ack)
	}
}

func TestReactionAddUnsupportedRetargetsThroughStatusRef(t *testing.T) {
	payload := `{"operation":"reaction_add","reaction":"eyes","targetMessageIdentity":{"conversationId":"D1","messageTs":"1700.1"},"statusDispatchRef":"status-3"}`
	web := &fakeWeb{
		reactionErr: &SlackError{Code: "cant_react"},
		historyFn: func(HistoryInput) ([]HistoryMessage, error) {
			return []HistoryMessage{{TS: "1698.0", ClientMsgID: "status-3"}}, nil
		},
	}

	// The original target rejects as unsupported; the retargeted reaction succeeds.
	ack := mutate(t, web, testDelivery("d-8", payload))
	if ack.Outcome != OutcomeDelivered {
		t.Fatalf("retargeted ack = %+v", ack)
	}
	if len(web.adds) != 2 || web.adds[1][2] != "1698.0" {
		t.Fatalf("adds = %+v, want a retarget to 1698.0", web.adds)
	}
}

func TestReactionAddMissingScopeFallsBackWhenMaterialExists(t *testing.T) {
	target := `"targetMessageIdentity":{"conversationId":"D1","messageTs":"1700.1"}`

	withMaterial := &fakeWeb{
		reactionErr: &SlackError{Code: "missing_scope"},
		postTS:      "1701.4",
	}
	scopePayload := `{"operation":"reaction_add","reaction":"eyes",` + target + `,"fallbackText":"no reactions here","fallbackDispatchRef":"fb-2"}`
	ack := mutate(t, withMaterial, testDelivery("d-9", scopePayload))
	if ack.Outcome != OutcomeDelivered || withMaterial.postCount() != 1 {
		t.Fatalf("missing-scope fallback ack = %+v", ack)
	}

	withoutMaterial := &fakeWeb{reactionErr: &SlackError{Code: "missing_scope"}}
	plainPayload := `{"operation":"reaction_add","reaction":"eyes",` + target + `}`
	ack = mutate(t, withoutMaterial, testDelivery("d-10", plainPayload))
	if ack.Outcome != OutcomeDelivered || withoutMaterial.postCount() != 0 {
		t.Fatal("missing scope without fallback material must degrade to delivered")
	}
}

func TestReactionOutcomesByErrorCode(t *testing.T) {
	target := `"targetMessageIdentity":{"conversationId":"D1","messageTs":"1700.1"},"reaction":"eyes"`

	transient := &fakeWeb{reactionErr: &SlackError{Code: "internal_error"}}
	ack := mutate(t, transient,
		testDelivery("d-11", `{"operation":"reaction_add",`+target+`}`))
	if ack.Outcome != OutcomeRetry || ack.Reason != "internal_error" {
		t.Fatalf("transient reaction ack = %+v", ack)
	}

	removeUnsupported := &fakeWeb{reactionErr: &SlackError{Code: "message_not_found"}}
	ack = mutate(t, removeUnsupported,
		testDelivery("d-12", `{"operation":"reaction_remove",`+target+`}`))
	if ack.Outcome != OutcomeDelivered {
		t.Fatalf("remove degradation ack = %+v", ack)
	}
}

func TestUploadFileIdentityFromSharesThenHistoryScan(t *testing.T) {
	content := base64.StdEncoding.EncodeToString([]byte("bytes"))
	payload := `{"operation":"upload_file","fileName":"f.bin","fileContentBase64":"` + content + `"}`

	publicShare := &fakeWeb{uploadResult: FileUploadResult{FileID: "F1", PublicShareTS: "1701.6"}}
	ack := mutate(t, publicShare, testDelivery("d-13", payload))
	if ack.ProviderMessageIdentity == nil || ack.ProviderMessageIdentity.MessageTs != "1701.6" {
		t.Fatalf("public share identity missing: %+v", ack)
	}

	privateShare := &fakeWeb{uploadResult: FileUploadResult{FileID: "F2", PrivateShareTS: "1701.7"}}
	ack = mutate(t, privateShare, testDelivery("d-14", payload))
	if ack.ProviderMessageIdentity == nil || ack.ProviderMessageIdentity.MessageTs != "1701.7" {
		t.Fatalf("private share identity missing: %+v", ack)
	}

	scanned := &fakeWeb{
		uploadResult: FileUploadResult{FileID: "F3"},
		historyFn: func(HistoryInput) ([]HistoryMessage, error) {
			return []HistoryMessage{{TS: "1701.8", FileIDs: []string{"F3"}}}, nil
		},
	}
	ack = mutate(t, scanned, testDelivery("d-15", payload))
	if ack.ProviderMessageIdentity == nil || ack.ProviderMessageIdentity.MessageTs != "1701.8" {
		t.Fatalf("history scan identity missing: %+v", ack)
	}

	rejected := &fakeWeb{uploadErr: &SlackError{Code: "file_uploads_limit_reached"}}
	ack = mutate(t, rejected, testDelivery("d-16", payload))
	if ack.Outcome != OutcomeRetry || ack.Reason != "file_uploads_limit_reached" {
		t.Fatalf("rejected upload ack = %+v", ack)
	}
}

func TestSegmentsPostSequentiallyAndOnlyFirstCarriesClientMsgID(t *testing.T) {
	successWeb := &fakeWeb{}
	segmentPayload := `{"segments":["one","two"],"clientMessageId":"cmid-seg","threadTs":"1700.0"}`
	delivery := &Delivery{ID: "d-17", ConversationID: testConversation, ThreadTs: strPtr("1700.0"), PayloadJSON: segmentPayload}
	ack := mutate(t, successWeb, delivery)
	if ack.Outcome != OutcomeDelivered || len(successWeb.posts) != 2 {
		t.Fatalf("segment ack = %+v posts = %d", ack, len(successWeb.posts))
	}
	if successWeb.posts[0].ClientMsgID != "cmid-seg" || successWeb.posts[1].ClientMsgID != "" {
		t.Fatalf("client msg id placement wrong: %+v / %+v", successWeb.posts[0], successWeb.posts[1])
	}
	if successWeb.posts[0].ThreadTs != "1700.0" {
		t.Fatalf("segment thread ts lost: %+v", successWeb.posts[0])
	}

	failing := &fakeWeb{postErr: &SlackError{Code: "msg_too_long"}}
	ack = mutate(t, failing, delivery)
	if ack.Outcome != OutcomeRetry || !strings.Contains(ack.Reason, "msg_too_long") {
		t.Fatalf("failed segment ack = %+v", ack)
	}
}

func TestUnknownOperationRoutesToReconciliation(t *testing.T) {
	// A realistic uncertain delivery carries at least one reconciliation key;
	// here the fallback ref locates the posted message.
	web := &fakeWeb{historyFn: func(HistoryInput) ([]HistoryMessage, error) {
		return []HistoryMessage{{TS: "1702.0", ClientMsgID: "f-8"}}, nil
	}}
	ack := mutate(t, web,
		testDelivery("d-18", `{"operation":"mystery","text":"?","fallbackDispatchRef":"f-8"}`))
	// Unknown operations settle without a provider identity even when found.
	if ack.Outcome != OutcomeDelivered || ack.ProviderMessageIdentity != nil {
		t.Fatalf("reconciled unknown operation ack = %+v", ack)
	}

	// Without any matching key the provider state cannot vouch for the
	// mutation, so the delivery must stay retriable.
	keylessWeb := &fakeWeb{historyFn: func(HistoryInput) ([]HistoryMessage, error) {
		return []HistoryMessage{{TS: "1702.1"}}, nil
	}}
	ack = mutate(t, keylessWeb,
		testDelivery("d-19", `{"operation":"mystery","text":"?"}`))
	if ack.Outcome != OutcomeRetry || ack.Reason != providerMutationAbsent {
		t.Fatalf("keyless reconcile ack = %+v", ack)
	}
}

func TestReconcileReactionsCompareProviderState(t *testing.T) {
	target := `"targetMessageIdentity":{"conversationId":"D1","messageTs":"1700.1"},"reaction":"eyes"`
	addPresent := `{"operation":"reaction_add",` + target + `}`
	addAbsent := addPresent // same payload; provider state decides

	presentWeb := &fakeWeb{reactionList: []string{"eyes", "tada"}}
	ack := reconcileNow(t, presentWeb, testDelivery("r-1", addPresent))
	if ack.Outcome != OutcomeDelivered || ack.ProviderMessageIdentity == nil {
		t.Fatalf("present reaction ack = %+v", ack)
	}

	absentWeb := &fakeWeb{}
	ack = reconcileNow(t, absentWeb, testDelivery("r-2", addAbsent))
	if ack.Outcome != OutcomeRetry || ack.Reason != providerMutationAbsent {
		t.Fatalf("absent reaction ack = %+v", ack)
	}

	removePresent := `{"operation":"reaction_remove",` + target + `}`
	ack = reconcileNow(t, presentWeb, testDelivery("r-3", removePresent))
	if ack.Outcome != OutcomeRetry {
		t.Fatalf("remove-present ack = %+v", ack)
	}

	unsupportedNoFallback := `{"operation":"reaction_add",` + target + `,"fallbackText":"fb"}`
	noRef := &fakeWeb{getErr: &SlackError{Code: "cant_react"}}
	ack = reconcileNow(t, noRef, testDelivery("r-4", unsupportedNoFallback))
	// Fallback text without fallbackDispatchRef degrades to delivered.
	if ack.Outcome != OutcomeDelivered || ack.ProviderMessageIdentity == nil {
		t.Fatalf("unsupported reconcile ack = %+v", ack)
	}
}

func TestReconcileMessagesMatchOnThreeKeys(t *testing.T) {
	cases := []struct {
		name    string
		payload string
		match   HistoryMessage
		wantTS  string
	}{
		{"by ts", `{"text":"a","providerMessageIdentity":{"conversationId":"D1","messageTs":"1703.1"}}`, HistoryMessage{TS: "1703.1"}, "1703.1"},
		{"by client msg id", `{"text":"a","clientMessageId":"c-1"}`, HistoryMessage{TS: "1703.2", ClientMsgID: "c-1"}, "1703.2"},
		{"by fallback ref", `{"text":"a","fallbackDispatchRef":"f-1"}`, HistoryMessage{TS: "1703.3", ClientMsgID: "f-1"}, "1703.3"},
	}
	for _, tc := range cases {
		web := &fakeWeb{historyFn: func(HistoryInput) ([]HistoryMessage, error) {
			return []HistoryMessage{tc.match}, nil
		}}
		ack := reconcileNow(t, web, testDelivery("rm-"+tc.name, tc.payload))
		if ack.Outcome != OutcomeDelivered || ack.ProviderMessageIdentity.MessageTs != tc.wantTS {
			t.Fatalf("%s: ack = %+v", tc.name, ack)
		}
	}
}

func TestReconcileChatUpdateVerifiesStoredText(t *testing.T) {
	matching := &fakeWeb{historyFn: func(HistoryInput) ([]HistoryMessage, error) {
		return []HistoryMessage{{TS: "1704.0", Text: "updated"}}, nil
	}}
	payload := `{"operation":"chat_update","text":"updated","providerMessageIdentity":{"conversationId":"D1","messageTs":"1704.0"}}`
	ack := reconcileNow(t, matching, testDelivery("rc-1", payload))
	if ack.Outcome != OutcomeDelivered {
		t.Fatalf("matching update ack = %+v", ack)
	}

	divergent := &fakeWeb{historyFn: func(HistoryInput) ([]HistoryMessage, error) {
		return []HistoryMessage{{TS: "1704.0", Text: "stale"}}, nil
	}}
	ack = reconcileNow(t, divergent, testDelivery("rc-2", payload))
	if ack.Outcome != OutcomeRetry || ack.Reason != providerMutationAbsent {
		t.Fatalf("divergent update ack = %+v", ack)
	}

	fallbackable := &fakeWeb{historyFn: func(HistoryInput) ([]HistoryMessage, error) { return nil, nil }, postTS: "1704.1"}
	fallbackPayload := `{"operation":"chat_update","text":"new","fallbackText":"orig","fallbackDispatchRef":"f-9"}`
	ack = reconcileNow(t, fallbackable, testDelivery("rc-3", fallbackPayload))
	if ack.Outcome != OutcomeDelivered || fallbackable.postCount() != 1 {
		t.Fatalf("fallback reconcile ack = %+v", ack)
	}
}

func TestParseDeliveryPayloadRejectsNonObjects(t *testing.T) {
	for _, bad := range []string{`[]`, `"text"`, `not json`} {
		if _, err := ParseDeliveryPayload(bad); err == nil {
			t.Fatalf("payload %q was accepted", bad)
		}
	}
	if _, err := ParseDeliveryPayload(`{"text":"ok"}`); err != nil {
		t.Fatalf("valid payload rejected: %v", err)
	}
}

func strPtr(value string) *string { return &value }
