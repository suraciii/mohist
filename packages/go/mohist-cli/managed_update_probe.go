package mohistcli

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"strings"
)

type realManagedControlPlane struct {
	client *client
}

func newRealManagedControlPlane(deps Dependencies) (*realManagedControlPlane, error) {
	config, err := ResolveConfig(deps)
	if err != nil {
		return nil, err
	}
	value, err := newClient(config, deps.HTTPClient)
	if err != nil {
		return nil, err
	}
	value.deps = deps
	if err := configureManagerClient(deps, value); err != nil {
		return nil, err
	}
	return &realManagedControlPlane{client: value}, nil
}

func (control *realManagedControlPlane) ObserveServer(ctx context.Context) (managedRuntimeObservation, error) {
	data, err := control.client.get(ctx, "/api/health")
	if err != nil {
		return managedRuntimeObservation{}, err
	}
	var health struct {
		Status         string `json:"status"`
		Version        string `json:"version"`
		GitHash        string `json:"gitHash"`
		TreeHash       string `json:"treeHash"`
		ArtifactDigest string `json:"artifactDigest"`
		ReleaseID      string `json:"releaseId"`
		Generation     int64  `json:"generation"`
	}
	if err := json.Unmarshal(data, &health); err != nil {
		return managedRuntimeObservation{}, fmt.Errorf("server health returned an invalid identity")
	}
	return managedRuntimeObservation{
		Status: health.Status,
		Identity: managedRuntimeIdentity{
			Component: "server", Version: health.Version, SourceRevision: health.GitHash,
			TreeHash: health.TreeHash, ArtifactDigest: health.ArtifactDigest,
			ReleaseID: health.ReleaseID, Generation: health.Generation,
		},
	}, nil
}

func (control *realManagedControlPlane) ObserveRunner(ctx context.Context, runnerID string) (managedRuntimeObservation, error) {
	if strings.TrimSpace(runnerID) == "" {
		return managedRuntimeObservation{}, fmt.Errorf("Runner identity is unavailable")
	}
	data, err := control.client.get(ctx, "/api/runner/identity?runnerId="+url.QueryEscape(runnerID))
	if err != nil {
		return managedRuntimeObservation{}, err
	}
	var runner struct {
		RunnerID             string `json:"runnerId"`
		BuildGitHash         string `json:"buildGitHash"`
		Component            string `json:"component"`
		Version              string `json:"version"`
		SourceRevision       string `json:"sourceRevision"`
		TreeHash             string `json:"treeHash"`
		ArtifactDigest       string `json:"artifactDigest"`
		ReleaseID            string `json:"releaseId"`
		Generation           int64  `json:"generation"`
		Status               string `json:"status"`
		ConnectionState      string `json:"connectionState"`
		ConnectionGeneration string `json:"connectionGeneration"`
	}
	if err := json.Unmarshal(data, &runner); err != nil {
		return managedRuntimeObservation{}, fmt.Errorf("Runner identity response was invalid")
	}
	return managedRuntimeObservation{
		Status: runner.Status, ConnectionState: runner.ConnectionState,
		ConnectionGeneration: runner.ConnectionGeneration,
		Identity: managedRuntimeIdentity{
			Component: runner.Component, Version: runner.Version,
			SourceRevision: runner.SourceRevision, TreeHash: runner.TreeHash,
			ArtifactDigest: runner.ArtifactDigest, ReleaseID: runner.ReleaseID,
			Generation: runner.Generation, RunnerID: runner.RunnerID,
			BuildGitHash: runner.BuildGitHash,
		},
	}, nil
}

func (control *realManagedControlPlane) BeginRunnerInterrupt(
	ctx context.Context,
	runnerID string,
	interruptID string,
) (managedRunnerInterrupt, error) {
	path := "/api/runner/" + url.PathEscape(runnerID) + "/update-interrupt"
	data, err := control.client.request(ctx, http.MethodPost, path, map[string]string{"updateInterruptId": interruptID})
	if err != nil {
		return managedRunnerInterrupt{}, err
	}
	var response struct {
		RunnerID        string `json:"runnerId"`
		Status          string `json:"status"`
		InterruptID     string `json:"updateInterruptId"`
		ActiveWorkCount int    `json:"activeWorkCount"`
	}
	if err := json.Unmarshal(data, &response); err != nil {
		return managedRunnerInterrupt{}, fmt.Errorf("Runner update interrupt response was invalid")
	}
	if response.RunnerID != runnerID || response.InterruptID != interruptID || response.Status != "draining" {
		return managedRunnerInterrupt{}, fmt.Errorf("Runner update interrupt was not confirmed")
	}
	return managedRunnerInterrupt{
		RunnerID: response.RunnerID, InterruptID: response.InterruptID,
		Status: response.Status, ActiveWorkCount: response.ActiveWorkCount,
	}, nil
}

func (control *realManagedControlPlane) CancelRunnerInterrupt(ctx context.Context, runnerID, interruptID string) error {
	path := "/api/runner/" + url.PathEscape(runnerID) + "/update-interrupt/" + url.PathEscape(interruptID) + "/cancel"
	data, err := control.client.request(ctx, http.MethodPost, path, map[string]any{})
	if err != nil {
		return err
	}
	var response struct {
		RunnerID    string `json:"runnerId"`
		InterruptID string `json:"updateInterruptId"`
		Status      string `json:"status"`
	}
	if json.Unmarshal(data, &response) != nil || response.RunnerID != runnerID || response.InterruptID != interruptID {
		return fmt.Errorf("Runner update interrupt cancellation response was invalid")
	}
	if response.Status != "cancelled" && response.Status != "already-cancelled" {
		return fmt.Errorf("Runner update interrupt cancellation was not confirmed")
	}
	return nil
}

func managedIdentityDifferences(actual, expected managedRuntimeIdentity) []string {
	differences := []string{}
	compare := func(name string, actualValue, expectedValue string) {
		if actualValue != expectedValue {
			differences = append(differences, name)
		}
	}
	compare("component", actual.Component, expected.Component)
	compare("version", actual.Version, expected.Version)
	compare("sourceRevision", actual.SourceRevision, expected.SourceRevision)
	compare("treeHash", actual.TreeHash, expected.TreeHash)
	compare("artifactDigest", actual.ArtifactDigest, expected.ArtifactDigest)
	compare("releaseId", actual.ReleaseID, expected.ReleaseID)
	if actual.Generation != expected.Generation {
		differences = append(differences, "generation")
	}
	if expected.Component == "runner" {
		compare("runnerId", actual.RunnerID, expected.RunnerID)
		compare("buildGitHash", actual.BuildGitHash, expected.BuildGitHash)
	}
	return differences
}

func verifyManagedObservation(
	component string,
	observation managedRuntimeObservation,
	expected managedRuntimeIdentity,
	previousConnectionGeneration string,
) error {
	if differences := managedIdentityDifferences(observation.Identity, expected); len(differences) > 0 {
		return fmt.Errorf("%s runtime identity differs in %s", component, strings.Join(differences, ", "))
	}
	if component == "server" {
		if observation.Status != "ok" {
			return fmt.Errorf("server health status is not ready")
		}
		return nil
	}
	if observation.Status != "online" || observation.ConnectionState != "connected" {
		return fmt.Errorf("Runner did not reconnect online")
	}
	if observation.ConnectionGeneration == "" || observation.ConnectionGeneration == previousConnectionGeneration {
		return fmt.Errorf("Runner did not establish a new connection generation")
	}
	return nil
}
