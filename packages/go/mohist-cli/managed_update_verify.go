package mohistcli

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"path/filepath"
	"sort"
	"strings"
)

// managedIdentityDocument is the read boundary for a RuntimeIdentity payload. It
// is deliberately separate from the canonical managedRuntimeIdentity type so the
// bounded v0 alias (gitHash) is never part of the writer contract.
type managedIdentityDocument struct {
	SchemaVersion  *int   `json:"schemaVersion"`
	Component      string `json:"component"`
	Version        string `json:"version"`
	SourceRevision string `json:"sourceRevision"`
	BuildGitHash   string `json:"buildGitHash"`
	GitHash        string `json:"gitHash"`
	TreeHash       string `json:"treeHash"`
	ArtifactDigest string `json:"artifactDigest"`
	ReleaseID      string `json:"releaseId"`
	Generation     int64  `json:"generation"`
	RunnerID       string `json:"runnerId"`
}

// readManagedIdentityDocument accepts a canonical RuntimeIdentity v1 payload or
// a bounded v0 legacy payload with no schemaVersion. Legacy payloads map
// buildGitHash from gitHash and sourceRevision from buildGitHash so an old
// release stays readable; the caller still compares the activated candidate
// canonically. A payload with any other schemaVersion is left as-is so canonical
// validation rejects it.
func readManagedIdentityDocument(value []byte) (managedRuntimeIdentity, bool, error) {
	var document managedIdentityDocument
	if err := json.Unmarshal(value, &document); err != nil {
		return managedRuntimeIdentity{}, false, err
	}
	identity := managedRuntimeIdentity{
		Component: document.Component, Version: document.Version,
		SourceRevision: document.SourceRevision, BuildGitHash: document.BuildGitHash,
		TreeHash: document.TreeHash, ArtifactDigest: document.ArtifactDigest,
		ReleaseID: document.ReleaseID, Generation: document.Generation, RunnerID: document.RunnerID,
	}
	if document.SchemaVersion != nil {
		identity.SchemaVersion = *document.SchemaVersion
		return identity, false, nil
	}
	if identity.BuildGitHash == "" {
		identity.BuildGitHash = document.GitHash
	}
	if identity.SourceRevision == "" {
		identity.SourceRevision = identity.BuildGitHash
	}
	return identity, true, nil
}

// verifyManagedActivatedTargets compares the four identity sources for a
// committed activation: the installed release manifest, the active.json target
// identity, and the observed Server/Runner runtime. It also requires every
// component activated by one transaction to agree on sourceRevision, treeHash,
// and generation. Any difference is returned so Update can route it into the
// existing rollback path.
func verifyManagedActivatedTargets(
	ctx context.Context,
	env managedUpdateEnvironment,
	runtimeRoot string,
	components []string,
	targets map[string]*managedRuntimeTarget,
) error {
	pointer, _, _, err := readManagedPointer(env.files, filepath.Join(runtimeRoot, "active.json"))
	if err != nil || pointerText(pointer, "status") != "verified" {
		return errors.New("managed activated runtime pointer is unavailable")
	}
	identities := map[string]managedRuntimeIdentity{}
	for _, component := range components {
		target := targets[component]
		if target == nil {
			return fmt.Errorf("managed %s activated target is unavailable", component)
		}
		if !validManagedRuntimeIdentity(target.Identity) {
			return fmt.Errorf("managed %s activated target is not canonical", component)
		}
		identityPath, err := managedTargetIdentityPath(target)
		if err != nil {
			return err
		}
		installed, err := readManagedReleaseIdentityFile(env.files, identityPath, component)
		if err != nil {
			return err
		}
		pointed, err := readManagedActivatedTarget(pointer, component)
		if err != nil {
			return fmt.Errorf("managed %s activated pointer target is unavailable: %w", component, err)
		}
		observation, err := observeManagedRuntime(ctx, env.control, component, target.Identity.RunnerID)
		if err != nil {
			return fmt.Errorf("managed %s runtime could not be observed after activation", component)
		}
		if err := verifyManagedActivatedComponent(component, target.Identity, installed, pointed.Identity, observation); err != nil {
			return err
		}
		identities[component] = target.Identity
	}
	return verifyManagedCrossComponentIdentities(identities)
}

// readManagedActivatedTarget reads a pointer target during post-activation
// verification. It accepts a canonical v1 identity or a bounded v0 legacy
// identity so an old active release stays readable; the caller still compares
// the result against the canonical activated candidate. The strict pointerTarget
// remains the authority outside this verification boundary.
func readManagedActivatedTarget(pointer managedPointer, component string) (*managedRuntimeTarget, error) {
	value := pointer[component]
	if len(value) == 0 || string(value) == "null" {
		return nil, errors.New("activated target is missing")
	}
	var target managedRuntimeTarget
	if err := json.Unmarshal(value, &target); err != nil {
		return nil, errors.New("activated target identity is invalid")
	}
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(value, &fields); err != nil {
		return nil, errors.New("activated target identity is invalid")
	}
	identity, _, err := readManagedIdentityDocument(fields["identity"])
	if err != nil {
		return nil, errors.New("activated target identity is invalid")
	}
	target.Identity = identity
	if target.Component == "" {
		target.Component = identity.Component
	}
	return &target, nil
}

// verifyManagedActivatedComponent asserts field-level equality between the
// canonical activated candidate and each observed identity source.
func verifyManagedActivatedComponent(
	component string,
	candidate managedRuntimeIdentity,
	installed managedRuntimeIdentity,
	pointed managedRuntimeIdentity,
	observation managedRuntimeObservation,
) error {
	differences := []string{}
	appendDifferences := func(source string, actual managedRuntimeIdentity) {
		for _, field := range managedIdentityDifferences(actual, candidate) {
			differences = append(differences, source+"."+field)
		}
	}
	appendDifferences("manifest", installed)
	appendDifferences("pointer", pointed)
	appendDifferences("runtime", observation.Identity)
	if len(differences) > 0 {
		return fmt.Errorf("managed %s activated identity differs in %s", component, strings.Join(differences, ", "))
	}
	if component == "server" {
		if observation.Status != "ok" {
			return errors.New("server health status is not ready after activation")
		}
		return nil
	}
	if observation.Status != "online" || observation.ConnectionState != "connected" {
		return errors.New("Runner did not reconnect after activation")
	}
	return nil
}

// verifyManagedCrossComponentIdentities requires every component activated by
// one transaction to agree on sourceRevision, treeHash, and generation.
func verifyManagedCrossComponentIdentities(identities map[string]managedRuntimeIdentity) error {
	components := make([]string, 0, len(identities))
	for component := range identities {
		components = append(components, component)
	}
	sort.Strings(components)
	for left := 0; left < len(components); left++ {
		for right := left + 1; right < len(components); right++ {
			a, b := identities[components[left]], identities[components[right]]
			if differences := managedCrossComponentDifferences(a, b); len(differences) > 0 {
				return fmt.Errorf(
					"managed %s and %s identities disagree in %s",
					components[left], components[right], strings.Join(differences, ", "),
				)
			}
		}
	}
	return nil
}

func managedCrossComponentDifferences(a, b managedRuntimeIdentity) []string {
	differences := []string{}
	if a.SourceRevision != b.SourceRevision {
		differences = append(differences, "sourceRevision")
	}
	if a.TreeHash != b.TreeHash {
		differences = append(differences, "treeHash")
	}
	if a.Generation != b.Generation {
		differences = append(differences, "generation")
	}
	return differences
}
