// Command mohist-slack runs the Mohist Slack adapter: one process serving
// every discovered Slack integration through per-target runtimes. See
// design/slack-go-port.md for the process contract.
package main

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"net/url"
	"os"
	"os/signal"
	"strconv"
	"strings"
	"syscall"
	"time"

	mohistslack "github.com/suraciii/mohist/packages/go/mohist-slack"
)

const defaultOperatorID = "mohist-slack"

// config is the resolved process configuration; the env contract lives in
// design/slack-go-port.md and mirrors the Node adapter's variables.
type config struct {
	adapterID      string
	serverURL      string
	operatorToken  string
	operatorID     string
	proxyURL       *url.URL
	heartbeatEvery time.Duration
	deliveryEvery  time.Duration
	discoveryEvery time.Duration
	maxInFlight    int
	logFormat      string
}

// envLookup abstracts the environment for tests.
type envLookup func(name string) (string, bool)

func osEnv(name string) (string, bool) {
	value, ok := os.LookupEnv(name)
	return value, ok
}

func resolveConfig(lookup envLookup, readCredentialFile func(string) (string, error)) (config, error) {
	cfg := config{
		serverURL:  "http://localhost:3456",
		operatorID: defaultOperatorID,
	}
	if value, ok := lookup("ADAPTER_ID"); ok && strings.TrimSpace(value) != "" {
		cfg.adapterID = strings.TrimSpace(value)
	} else {
		cfg.adapterID = fmt.Sprintf("mohist-slack-%d", os.Getpid())
	}
	if value, ok := lookup("SERVER_URL"); ok && strings.TrimSpace(value) != "" {
		cfg.serverURL = strings.TrimSpace(value)
	}

	direct := firstNonBlank(lookupValue(lookup, "MOHIST_OPERATOR_TOKEN"), lookupValue(lookup, "OPERATOR_TOKEN"))
	switch {
	case direct != "":
		cfg.operatorToken = direct
	default:
		path := strings.TrimSpace(lookupValue(lookup, "MOHIST_OPERATOR_TOKEN_PATH"))
		if path == "" {
			return cfg, errors.New("Mohist operator credential is required")
		}
		fileToken, err := readCredentialFile(path)
		if err != nil {
			return cfg, errors.New("Mohist operator credential file could not be read")
		}
		token := strings.TrimSpace(fileToken)
		if token == "" {
			return cfg, errors.New("Mohist operator credential is invalid")
		}
		cfg.operatorToken = token
	}

	if value, ok := lookup("MOHIST_OPERATOR_ID"); ok && strings.TrimSpace(value) != "" {
		cfg.operatorID = strings.TrimSpace(value)
	}

	if value, ok := lookup("SLACK_PROXY_URL"); ok && strings.TrimSpace(value) != "" {
		parsed, err := url.Parse(strings.TrimSpace(value))
		if err != nil || parsed.Scheme == "" || parsed.Host == "" {
			return cfg, errors.New("SLACK_PROXY_URL must be an absolute proxy URL")
		}
		parsed.Scheme = strings.ToLower(parsed.Scheme)
		if parsed.Scheme != "http" && parsed.Scheme != "socks5" {
			return cfg, errors.New("SLACK_PROXY_URL scheme must be http or socks5")
		}
		if parsed.User != nil {
			if _, hasPassword := parsed.User.Password(); !hasPassword {
				parsed.User = url.UserPassword(parsed.User.Username(), "")
			}
		}
		cfg.proxyURL = parsed
	}

	var err error
	if cfg.heartbeatEvery, err = positiveDuration(lookup, "HEARTBEAT_INTERVAL_MS", 15*time.Second, time.Second); err != nil {
		return cfg, err
	}
	if cfg.deliveryEvery, err = positiveDuration(lookup, "DELIVERY_POLL_INTERVAL_MS", time.Second, 100*time.Millisecond); err != nil {
		return cfg, err
	}
	if cfg.discoveryEvery, err = positiveDuration(lookup, "DISCOVERY_POLL_INTERVAL_MS", 15*time.Second, time.Second); err != nil {
		return cfg, err
	}
	cfg.maxInFlight = 8
	if value, ok := lookup("MAX_IN_FLIGHT"); ok && strings.TrimSpace(value) != "" {
		parsed, parseErr := strconv.Atoi(strings.TrimSpace(value))
		if parseErr != nil || parsed < 1 {
			return cfg, errors.New("MAX_IN_FLIGHT must be a positive integer")
		}
		cfg.maxInFlight = parsed
	}
	if value, ok := lookup("MOHIST_LOG_FORMAT"); ok {
		cfg.logFormat = strings.TrimSpace(value)
	}
	return cfg, nil
}

func lookupValue(lookup envLookup, name string) string {
	value, _ := lookup(name)
	return value
}

func firstNonBlank(values ...string) string {
	for _, value := range values {
		if strings.TrimSpace(value) != "" {
			return strings.TrimSpace(value)
		}
	}
	return ""
}

func positiveDuration(lookup envLookup, name string, fallback, floor time.Duration) (time.Duration, error) {
	raw, ok := lookup(name)
	if !ok || strings.TrimSpace(raw) == "" {
		return fallback, nil
	}
	millis, err := strconv.Atoi(strings.TrimSpace(raw))
	if err != nil || millis <= 0 {
		return 0, fmt.Errorf("%s must be a positive integer of milliseconds", name)
	}
	duration := time.Duration(millis) * time.Millisecond
	if duration < floor {
		duration = floor
	}
	return duration, nil
}

func main() {
	logger := mohistslack.NewLogger(os.Stderr, os.Getenv("MOHIST_LOG_FORMAT"))
	slog.SetDefault(logger)

	cfg, err := resolveConfig(osEnv, func(path string) (string, error) {
		content, err := os.ReadFile(path)
		return string(content), err
	})
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		os.Exit(1)
	}

	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()

	if err := run(ctx, cfg, logger); err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		os.Exit(1)
	}
}

// run assembles the adapter over the Server transport and serves until the
// signal context aborts.
func run(ctx context.Context, cfg config, logger *slog.Logger) error {
	serverAPI, err := mohistslack.NewServerAPI(
		cfg.serverURL,
		cfg.operatorToken,
		cfg.operatorID,
	)
	if err != nil {
		return err
	}

	httpClient := &http.Client{Timeout: 30 * time.Second}
	if cfg.proxyURL != nil {
		transport := http.DefaultTransport.(*http.Transport).Clone()
		transport.Proxy = http.ProxyURL(cfg.proxyURL)
		httpClient.Transport = transport
	}

	adapter := mohistslack.NewAdapter(mohistslack.AdapterOptions{
		AdapterID:      cfg.adapterID,
		Transport:      serverAPI,
		SocketFactory:  newSocketFactory(cfg.proxyURL),
		WebFactory:     newWebFactory(httpClient),
		Logger:         logger,
		DiscoveryEvery: cfg.discoveryEvery,
		HeartbeatEvery: cfg.heartbeatEvery,
		DeliveryPoll:   cfg.deliveryEvery,
		MaxInFlight:    cfg.maxInFlight,
		Dispose:        httpClient.CloseIdleConnections,
	})

	if err := adapter.Start(ctx); err != nil {
		return err
	}
	<-ctx.Done()
	adapter.Stop()
	return nil
}

func newSocketFactory(proxyURL *url.URL) mohistslack.SocketFactory {
	return func(appToken string, _ mohistslack.Target) mohistslack.SocketClient {
		socket := mohistslack.NewSlackSocket(appToken)
		if proxyURL != nil {
			socket.SetProxy(proxyURL)
		}
		return socket
	}
}

func newWebFactory(httpClient *http.Client) mohistslack.WebFactory {
	return func(botToken string, _ mohistslack.Target) mohistslack.WebClient {
		return mohistslack.NewSlackWeb(botToken, httpClient)
	}
}
