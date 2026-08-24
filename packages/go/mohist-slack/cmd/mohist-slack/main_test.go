package main

import (
	"errors"
	"testing"
	"time"
)

func envOf(entries map[string]string) envLookup {
	return func(name string) (string, bool) {
		value, ok := entries[name]
		return value, ok
	}
}

func readOK(path string) (string, error) { return "file-token\n", nil }

func TestResolveConfigAppliesDefaults(t *testing.T) {
	cfg, err := resolveConfig(envOf(map[string]string{
		"MOHIST_OPERATOR_TOKEN": "op-token",
	}), readOK)
	if err != nil {
		t.Fatalf("resolveConfig() error = %v", err)
	}
	if cfg.serverURL != "http://localhost:3456" {
		t.Fatalf("serverURL = %q", cfg.serverURL)
	}
	if cfg.adapterID == "" {
		t.Fatal("adapterID must always resolve")
	}
	if cfg.operatorID != defaultOperatorID {
		t.Fatalf("operatorID = %q", cfg.operatorID)
	}
	if cfg.heartbeatEvery != 15*time.Second || cfg.deliveryEvery != time.Second || cfg.discoveryEvery != 15*time.Second {
		t.Fatalf("intervals = %v %v %v", cfg.heartbeatEvery, cfg.deliveryEvery, cfg.discoveryEvery)
	}
	if cfg.maxInFlight != 8 {
		t.Fatalf("maxInFlight = %d", cfg.maxInFlight)
	}
}

func TestResolveConfigTokenPrecedence(t *testing.T) {
	direct, err := resolveConfig(envOf(map[string]string{
		"MOHIST_OPERATOR_TOKEN":      "direct ",
		"OPERATOR_TOKEN":             "legacy",
		"MOHIST_OPERATOR_TOKEN_PATH": "/tmp/ignored",
	}), func(string) (string, error) { return "from-file", nil })
	if err != nil {
		t.Fatalf("resolveConfig() error = %v", err)
	}
	if direct.operatorToken != "direct" {
		t.Fatalf("direct token = %q", direct.operatorToken)
	}

	legacy, err := resolveConfig(envOf(map[string]string{"OPERATOR_TOKEN": " legacy "}), readOK)
	if err != nil {
		t.Fatalf("resolveConfig() error = %v", err)
	}
	if legacy.operatorToken != "legacy" {
		t.Fatalf("legacy token = %q", legacy.operatorToken)
	}

	fromFile, err := resolveConfig(envOf(map[string]string{"MOHIST_OPERATOR_TOKEN_PATH": "/run/token"}), readOK)
	if err != nil {
		t.Fatalf("resolveConfig() error = %v", err)
	}
	if fromFile.operatorToken != "file-token" {
		t.Fatalf("file token = %q (want trimmed)", fromFile.operatorToken)
	}

	if _, err := resolveConfig(envOf(map[string]string{}), readOK); err == nil {
		t.Fatal("missing credential was accepted")
	}
	unreadable := func(string) (string, error) { return "", errors.New("denied") }
	if _, err := resolveConfig(envOf(map[string]string{"MOHIST_OPERATOR_TOKEN_PATH": "/x"}), unreadable); err == nil {
		t.Fatal("unreadable credential file was accepted")
	}
	blank := func(string) (string, error) { return "   ", nil }
	if _, err := resolveConfig(envOf(map[string]string{"MOHIST_OPERATOR_TOKEN_PATH": "/x"}), blank); err == nil {
		t.Fatal("blank credential file was accepted")
	}
}

func TestResolveConfigFloorsAndOverrides(t *testing.T) {
	cfg, err := resolveConfig(envOf(map[string]string{
		"MOHIST_OPERATOR_TOKEN":      "op",
		"HEARTBEAT_INTERVAL_MS":      "5",    // below the 1s floor
		"DELIVERY_POLL_INTERVAL_MS":  "50",   // below the 100ms floor
		"DISCOVERY_POLL_INTERVAL_MS": "2500", // above the floor → honored
		"MAX_IN_FLIGHT":              "3",
		"MOHIST_OPERATOR_ID":         "custom-operator",
		"SERVER_URL":                 "http://127.0.0.1:9999",
		"MOHIST_LOG_FORMAT":          "json",
	}), readOK)
	if err != nil {
		t.Fatalf("resolveConfig() error = %v", err)
	}
	if cfg.heartbeatEvery != time.Second {
		t.Fatalf("heartbeat = %v, want floored to 1s", cfg.heartbeatEvery)
	}
	if cfg.deliveryEvery != 100*time.Millisecond {
		t.Fatalf("delivery = %v, want floored to 100ms", cfg.deliveryEvery)
	}
	if cfg.discoveryEvery != 2500*time.Millisecond {
		t.Fatalf("discovery = %v", cfg.discoveryEvery)
	}
	if cfg.maxInFlight != 3 || cfg.operatorID != "custom-operator" || cfg.logFormat != "json" {
		t.Fatalf("overrides lost: %+v", cfg)
	}
	if _, err := resolveConfig(envOf(map[string]string{
		"MOHIST_OPERATOR_TOKEN": "op",
		"MAX_IN_FLIGHT":         "zero",
	}), readOK); err == nil {
		t.Fatal("non-numeric MAX_IN_FLIGHT was accepted")
	}
	if _, err := resolveConfig(envOf(map[string]string{
		"MOHIST_OPERATOR_TOKEN": "op",
		"SLACK_PROXY_URL":       "not a url",
	}), readOK); err == nil {
		t.Fatal("invalid proxy URL was accepted")
	}
	if _, err := resolveConfig(envOf(map[string]string{
		"MOHIST_OPERATOR_TOKEN": "op",
		"SLACK_PROXY_URL":       "https://proxy.example",
	}), readOK); err == nil {
		t.Fatal("proxy scheme unsupported by the Socket Mode dialer was accepted")
	}
	proxied, err := resolveConfig(envOf(map[string]string{
		"MOHIST_OPERATOR_TOKEN": "op",
		"SLACK_PROXY_URL":       "http://127.0.0.1:8080",
	}), readOK)
	if err != nil || proxied.proxyURL == nil {
		t.Fatalf("proxy not resolved: %+v err=%v", proxied, err)
	}
	proxied, err = resolveConfig(envOf(map[string]string{
		"MOHIST_OPERATOR_TOKEN": "op",
		"SLACK_PROXY_URL":       "http://proxy-user@127.0.0.1:8080",
	}), readOK)
	if err != nil || proxied.proxyURL == nil {
		t.Fatalf("credentialed proxy not resolved: %+v err=%v", proxied, err)
	}
	if password, explicit := proxied.proxyURL.User.Password(); !explicit || password != "" {
		t.Fatal("username-only proxy credentials were not normalized with an explicit empty password")
	}
}
