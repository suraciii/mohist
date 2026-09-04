package mohistcli

import (
	"encoding/base64"
	"encoding/json"
	"io"
	"net/http"
	"strings"
	"testing"
)

func TestManagerBrokerCodecPreservesHTTPContract(t *testing.T) {
	req, err := http.NewRequest(http.MethodPost, "https://mohist.test/api/slack-manager/reply", strings.NewReader(`{"text":"hello"}`))
	if err != nil {
		t.Fatal(err)
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("X-Mohist-Manager-Mode", "1")

	encoded, err := encodeManagerBrokerRequest(req)
	if err != nil {
		t.Fatal(err)
	}
	var brokerRequest managerBrokerRequest
	if err := json.Unmarshal(encoded, &brokerRequest); err != nil {
		t.Fatal(err)
	}
	if brokerRequest.Method != http.MethodPost || brokerRequest.URL != req.URL.String() {
		t.Fatalf("broker request=%+v", brokerRequest)
	}
	if brokerRequest.Headers["X-Mohist-Manager-Mode"] != "1" || brokerRequest.Headers["Authorization"] != "" {
		t.Fatalf("broker headers=%v", brokerRequest.Headers)
	}
	body, err := base64.StdEncoding.DecodeString(brokerRequest.BodyBase64)
	if err != nil || string(body) != `{"text":"hello"}` {
		t.Fatalf("body=%q err=%v", body, err)
	}

	responseBytes, err := json.Marshal(managerBrokerResponse{
		Status:     http.StatusAccepted,
		Headers:    map[string]string{"Content-Type": "application/json"},
		BodyBase64: base64.StdEncoding.EncodeToString([]byte(`{"accepted":true}`)),
	})
	if err != nil {
		t.Fatal(err)
	}
	resp, err := decodeManagerBrokerResponse(req, responseBytes)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	responseBody, err := io.ReadAll(resp.Body)
	if err != nil {
		t.Fatal(err)
	}
	if resp.StatusCode != http.StatusAccepted || resp.Header.Get("Content-Type") != "application/json" || string(responseBody) != `{"accepted":true}` {
		t.Fatalf("response status=%d headers=%v body=%q", resp.StatusCode, resp.Header, responseBody)
	}
}
