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

	for _, tc := range []struct {
		name      string
		operation string
		code      string
	}{
		{name: "add already present", operation: OpReactionAdd, code: "already_reacted"},
		{name: "remove already absent", operation: OpReactionRemove, code: "no_reaction"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			web := &fakeWeb{reactionErr: &SlackError{Code: tc.code}}
			ack := mutate(t, web,
				testDelivery("d-idempotent", `{"operation":"`+tc.operation+`",`+target+`}`))
			if ack.Outcome != OutcomeDelivered {
				t.Fatalf("idempotent reaction ack = %+v", ack)
			}
		})
	}

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

func TestSegmentsPostSequentiallyWithDeterministicClientMsgIDs(t *testing.T) {
	successWeb := &fakeWeb{}
	segmentPayload := `{"segments":["one","two","three"],"clientMessageId":"cmid-seg","threadTs":"1700.0"}`
	delivery := &Delivery{ID: "d-17", ConversationID: testConversation, ThreadTs: strPtr("1700.0"), PayloadJSON: segmentPayload}
	ack := mutate(t, successWeb, delivery)
	if ack.Outcome != OutcomeDelivered || len(successWeb.posts) != 3 {
		t.Fatalf("segment ack = %+v posts = %d", ack, len(successWeb.posts))
	}
	payload, err := ParseDeliveryPayload(segmentPayload)
	if err != nil {
		t.Fatalf("parse segment payload: %v", err)
	}
	refs, err := segmentDispatchRefs(payload)
	if err != nil {
		t.Fatalf("derive segment refs: %v", err)
	}
	for index, post := range successWeb.posts {
		if post.ClientMsgID != refs[index] {
			t.Fatalf("segment %d client msg id = %q, want %q", index, post.ClientMsgID, refs[index])
		}
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

func TestSegmentDispatchReferenceRejectsSeparatorCollisionBeforePosting(t *testing.T) {
	derived, err := segmentDispatchRef("x", 1)
	if err != nil {
		t.Fatalf("derive segment reference: %v", err)
	}
	if derived != "x"+segmentDispatchSeparator+"1" {
		t.Fatalf("derived reference = %q", derived)
	}
	if _, err := segmentDispatchRef("x"+segmentDispatchSeparator+"1", 0); err == nil {
		t.Fatal("a producer base containing the separator must be rejected")
	}

	web := &fakeWeb{}
	payload := `{"segments":["one","two"],"clientMessageId":"x\u001f1"}`
	if _, err := MutateDelivery(context.Background(), web, testDelivery("d-17a", payload), func() {}); err == nil {
		t.Fatal("invalid segment base was accepted")
	}
	if web.postCount() != 0 {
		t.Fatalf("invalid segment base posted %d messages", web.postCount())
	}
}

func TestReconcileSegmentsRequiresEverySegmentReference(t *testing.T) {
	payload := `{"operation":"post_message","segments":["one","two","three"],"clientMessageId":"cmid-seg-reconcile"}`
	missing := &fakeWeb{historyFn: func(HistoryInput) ([]HistoryMessage, error) {
		return []HistoryMessage{{TS: "1702.2", ClientMsgID: "cmid-seg-reconcile"}}, nil
	}}
	ack := reconcileNow(t, missing, testDelivery("d-17b", payload))
	if ack.Outcome != OutcomeRetry || ack.Reason != providerMutationAbsent {
		t.Fatalf("partial segment history ack = %+v", ack)
	}

	complete := &fakeWeb{historyFn: func(HistoryInput) ([]HistoryMessage, error) {
		return []HistoryMessage{
			{TS: "1702.3", ClientMsgID: "cmid-seg-reconcile"},
			{TS: "1702.4", ClientMsgID: "cmid-seg-reconcile" + segmentDispatchSeparator + "1"},
			{TS: "1702.5", ClientMsgID: "cmid-seg-reconcile" + segmentDispatchSeparator + "2"},
		}, nil
	}}
	ack = reconcileNow(t, complete, testDelivery("d-17c", payload))
	if ack.Outcome != OutcomeDelivered || ack.ProviderMessageIdentity == nil || ack.ProviderMessageIdentity.MessageTs != "1702.3" {
		t.Fatalf("complete segment history ack = %+v", ack)
	}
}

func TestFallbackLookupPaginatesBeforePosting(t *testing.T) {
	web := &fakeWeb{reactionErr: &SlackError{Code: "cant_react"}, postTS: "1702.7"}
	web.historyPageFn = func(input HistoryInput) (HistoryPage, error) {
		switch input.Cursor {
		case "":
			newer := make([]HistoryMessage, 200)
			for index := range newer {
				newer[index] = HistoryMessage{TS: "1702.8", ClientMsgID: "newer"}
			}
			return HistoryPage{
				Messages:   newer,
				HasMore:    true,
				NextCursor: "page-2",
			}, nil
		case "page-2":
			return HistoryPage{Messages: []HistoryMessage{{TS: "1702.9", ClientMsgID: "fb-deep"}}}, nil
		default:
			return HistoryPage{}, errors.New("unexpected history cursor")
		}
	}
	payload := `{"operation":"reaction_add","reaction":"eyes","targetMessageIdentity":{"conversationId":"D1","messageTs":"1700.1"},"fallbackText":"fallback","fallbackDispatchRef":"fb-deep"}`
	ack := mutate(t, web, testDelivery("d-17lookup", payload))
	if ack.Outcome != OutcomeDelivered || ack.ProviderMessageIdentity == nil || ack.ProviderMessageIdentity.MessageTs != "1702.9" {
		t.Fatalf("paginated fallback ack = %+v", ack)
	}
	if web.postCount() != 0 || len(web.historyInputs) != 2 || web.historyInputs[1].Cursor != "page-2" {
		t.Fatalf("paginated fallback calls = %+v posts = %d", web.historyInputs, web.postCount())
	}
}

func TestFallbackLookupIncompleteSettlesUncertainWithoutPosting(t *testing.T) {
	web := &fakeWeb{reactionErr: &SlackError{Code: "cant_react"}, postTS: "1703.0"}
	web.historyPageFn = func(input HistoryInput) (HistoryPage, error) {
		if input.Cursor == "" {
			return HistoryPage{HasMore: true, NextCursor: "page-2"}, nil
		}
		return HistoryPage{HasMore: true}, nil
	}
	payload := `{"operation":"reaction_add","reaction":"eyes","targetMessageIdentity":{"conversationId":"D1","messageTs":"1700.1"},"fallbackText":"fallback","fallbackDispatchRef":"fb-missing"}`
	ack := mutate(t, web, testDelivery("d-17incomplete", payload))
	if ack.Outcome != OutcomeUncertain || ack.Reason != providerHistoryIncomplete {
		t.Fatalf("incomplete fallback ack = %+v", ack)
	}
	if web.postCount() != 0 {
		t.Fatalf("incomplete fallback posted %d messages", web.postCount())
	}
}

func TestSegmentReconciliationPaginatesAndRequiresCompleteHistory(t *testing.T) {
	payload := `{"operation":"post_message","segments":["one","two","three"],"clientMessageId":"cmid-page"}`
	web := &fakeWeb{}
	web.historyPageFn = func(input HistoryInput) (HistoryPage, error) {
		switch input.Cursor {
		case "":
			return HistoryPage{
				Messages:   []HistoryMessage{{TS: "1703.1", ClientMsgID: "cmid-page"}},
				HasMore:    true,
				NextCursor: "page-2",
			}, nil
		case "page-2":
			return HistoryPage{Messages: []HistoryMessage{
				{TS: "1703.2", ClientMsgID: "cmid-page" + segmentDispatchSeparator + "1"},
				{TS: "1703.3", ClientMsgID: "cmid-page" + segmentDispatchSeparator + "2"},
			}}, nil
		default:
			return HistoryPage{}, errors.New("unexpected history cursor")
		}
	}
	ack := reconcileNow(t, web, testDelivery("d-17page", payload))
	if ack.Outcome != OutcomeDelivered || ack.ProviderMessageIdentity == nil || ack.ProviderMessageIdentity.MessageTs != "1703.1" {
		t.Fatalf("paginated segment ack = %+v", ack)
	}
	if len(web.historyInputs) != 2 || web.historyInputs[1].Cursor != "page-2" {
		t.Fatalf("segment history inputs = %+v", web.historyInputs)
	}

	incomplete := &fakeWeb{}
	incomplete.historyPageFn = func(input HistoryInput) (HistoryPage, error) {
		if input.Cursor == "" {
			return HistoryPage{Messages: []HistoryMessage{{TS: "1703.4", ClientMsgID: "cmid-page"}}, HasMore: true, NextCursor: "page-2"}, nil
		}
		return HistoryPage{HasMore: true}, nil
	}
	ack = reconcileNow(t, incomplete, testDelivery("d-17page-incomplete", payload))
	if ack.Outcome != OutcomeUncertain || ack.Reason != providerHistoryIncomplete {
		t.Fatalf("incomplete segment ack = %+v", ack)
	}
}

func TestFallbackReconciliationFindsLostPostBeforePostingAgain(t *testing.T) {
	web := &fakeWeb{
		reactionErr: &SlackError{Code: "cant_react"},
		getErr:      &SlackError{Code: "cant_react"},
		postErr:     errors.New("response lost"),
	}
	web.historyFn = func(HistoryInput) ([]HistoryMessage, error) {
		if web.postCount() == 0 {
			return nil, nil
		}
		return []HistoryMessage{{TS: "1702.6", ClientMsgID: "fb-lost"}}, nil
	}
	payload := `{"operation":"reaction_add","reaction":"eyes","targetMessageIdentity":{"conversationId":"D1","messageTs":"1700.1"},"fallbackText":"fallback","fallbackDispatchRef":"fb-lost"}`
	delivery := testDelivery("d-17d", payload)
	if _, err := MutateDelivery(context.Background(), web, delivery, func() {}); err == nil {
		t.Fatal("lost fallback response should leave the first delivery uncertain")
	}
	ack := reconcileNow(t, web, delivery)
	if ack.Outcome != OutcomeDelivered || ack.ProviderMessageIdentity == nil || ack.ProviderMessageIdentity.MessageTs != "1702.6" {
		t.Fatalf("reconciled fallback ack = %+v", ack)
	}
	if web.postCount() != 1 {
		t.Fatalf("fallback was posted more than once: %d", web.postCount())
	}
}

func TestFallbackDispatchReferenceDerivesFromPrimaryReference(t *testing.T) {
	web := &fakeWeb{reactionErr: &SlackError{Code: "missing_scope"}}
	payload := `{"operation":"reaction_add","reaction":"eyes","targetMessageIdentity":{"conversationId":"D1","messageTs":"1700.1"},"clientMessageId":"cmid-fallback","fallbackText":"fallback"}`
	ack := mutate(t, web, testDelivery("d-17e", payload))
	if ack.Outcome != OutcomeDelivered || web.postCount() != 1 || web.posts[0].ClientMsgID != "cmid-fallback:fallback" {
		t.Fatalf("derived fallback ack = %+v post = %+v", ack, web.posts)
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
