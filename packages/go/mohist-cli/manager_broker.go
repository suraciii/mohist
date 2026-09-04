package mohistcli

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"io"
	"net"
	"net/http"
)

type managerCredentialTransport struct {
	broker ManagerCredentialBroker
}

func (t managerCredentialTransport) RoundTrip(req *http.Request) (*http.Response, error) {
	return t.broker(req.Context(), req)
}

type managerBrokerRequest struct {
	Method     string            `json:"method"`
	URL        string            `json:"url"`
	Headers    map[string]string `json:"headers,omitempty"`
	BodyBase64 string            `json:"bodyBase64,omitempty"`
}

type managerBrokerResponse struct {
	Status     int               `json:"status"`
	Headers    map[string]string `json:"headers"`
	BodyBase64 string            `json:"bodyBase64"`
}

func unixManagerCredentialBroker(socketPath string) ManagerCredentialBroker {
	return func(ctx context.Context, req *http.Request) (*http.Response, error) {
		var body []byte
		var err error
		if req.Body != nil {
			body, err = io.ReadAll(req.Body)
			if err != nil {
				return nil, err
			}
		}
		brokerRequest := managerBrokerRequest{
			Method:  req.Method,
			URL:     req.URL.String(),
			Headers: make(map[string]string, len(req.Header)),
		}
		if len(body) > 0 && req.Method != http.MethodGet && req.Method != http.MethodHead {
			brokerRequest.BodyBase64 = base64.StdEncoding.EncodeToString(body)
		}
		for name, values := range req.Header {
			if len(values) > 0 {
				brokerRequest.Headers[name] = values[0]
			}
		}
		encoded, err := json.Marshal(brokerRequest)
		if err != nil {
			return nil, err
		}

		conn, err := (&net.Dialer{}).DialContext(ctx, "unix", socketPath)
		if err != nil {
			return nil, err
		}
		defer conn.Close()
		if _, err := conn.Write(encoded); err != nil {
			return nil, err
		}
		if unix, ok := conn.(*net.UnixConn); ok {
			if err := unix.CloseWrite(); err != nil {
				return nil, err
			}
		}
		responseBytes, err := io.ReadAll(conn)
		if err != nil {
			return nil, err
		}
		var brokerResponse managerBrokerResponse
		if err := json.Unmarshal(responseBytes, &brokerResponse); err != nil || brokerResponse.Status == 0 {
			return nil, errors.New("Manager credential broker returned an invalid response")
		}
		responseBody, err := base64.StdEncoding.DecodeString(brokerResponse.BodyBase64)
		if err != nil {
			return nil, errors.New("Manager credential broker returned invalid response content")
		}
		headers := make(http.Header, len(brokerResponse.Headers))
		for name, value := range brokerResponse.Headers {
			headers.Set(name, value)
		}
		return &http.Response{
			StatusCode: brokerResponse.Status,
			Status:     http.StatusText(brokerResponse.Status),
			Header:     headers,
			Body:       io.NopCloser(bytes.NewReader(responseBody)),
			Request:    req,
		}, nil
	}
}

func isManagerMode(lookup EnvLookup) bool {
	value, _ := lookup("MOHIST_MANAGER_MODE")
	return value == "1" || value == "true" || value == "TRUE" || value == "yes" || value == "YES"
}
