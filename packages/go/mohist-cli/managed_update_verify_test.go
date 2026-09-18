package mohistcli

import (
	"context"
	"encoding/json"
	"path/filepath"
	"strings"
	"testing"
)

func managedVerifyTestIdentity(component, sourceRevision, treeHash string, generation int64) managedRuntimeIdentity {
	identity := managedRuntimeIdentity{
		SchemaVersion: 1, Component: component, Version: "0.0.0+" + sourceRevision,
		SourceRevision: sourceRevision, BuildGitHash: sourceRevision, TreeHash: treeHash,
		ArtifactDigest: strings.Repeat("a", 64), ReleaseID: "mohist-" + component + "-" + sourceRevision,
		Generation: generation,
	}
	if component == "runner" {
		identity.RunnerID = "runner-1"
	}
	return identity
}

func managedVerifyTestTarget(component string, identity managedRuntimeIdentity) *managedRuntimeTarget {
	root := filepath.Join("/runtime/releases", component)
	target := &managedRuntimeTarget{
		Component: component, Entrypoint: filepath.Join(root, "entry"), WorkingDirectory: root,
		Arguments: []string{}, RuntimeIdentifier: "linux-x64", Identity: identity,
		IsAbsoluteTarget: true, UsesCanonicalEntrypoint: true,
	}
	if component == "runner" {
		target.DependencyRoot = &root
	}
	return target
}

func managedVerifyMatchingObservation(identity managedRuntimeIdentity) managedRuntimeObservation {
	observation := managedRuntimeObservation{
		Identity: identity, Status: "ok", ConnectionState: "connected", ConnectionGeneration: "connection-1",
	}
	if identity.Component == "runner" {
		observation.Status = "online"
	}
	return observation
}

type managedVerifyTestControl struct {
	observations map[string]managedRuntimeObservation
}

func (control managedVerifyTestControl) ObserveServer(context.Context) (managedRuntimeObservation, error) {
	return control.observations["server"], nil
}

func (control managedVerifyTestControl) ObserveRunner(context.Context, string) (managedRuntimeObservation, error) {
	return control.observations["runner"], nil
}

func (managedVerifyTestControl) BeginRunnerInterrupt(context.Context, string, string) (managedRunnerInterrupt, error) {
	return managedRunnerInterrupt{}, nil
}

func (managedVerifyTestControl) CancelRunnerInterrupt(context.Context, string, string) error {
	return nil
}

func managedVerifyTestEnvironment(
	t *testing.T,
	serverIdentity managedRuntimeIdentity,
	runnerIdentity managedRuntimeIdentity,
) (managedUpdateEnvironment, string, map[string]*managedRuntimeTarget, map[string]managedRuntimeObservation, *managedBuildTestFiles) {
	t.Helper()
	targets := map[string]*managedRuntimeTarget{
		"server": managedVerifyTestTarget("server", serverIdentity),
		"runner": managedVerifyTestTarget("runner", runnerIdentity),
	}
	observations := map[string]managedRuntimeObservation{
		"server": managedVerifyMatchingObservation(serverIdentity),
		"runner": managedVerifyMatchingObservation(runnerIdentity),
	}
	files := newManagedBuildTestFiles()
	runtimeRoot := "/runtime"
	writeManagedVerifyPointer(t, files, runtimeRoot, targets)
	for _, target := range targets {
		writeManagedVerifyManifest(t, files, target, target.Identity)
	}
	env := managedUpdateEnvironment{files: files, control: managedVerifyTestControl{observations: observations}}
	return env, runtimeRoot, targets, observations, files
}

func writeManagedVerifyPointer(t *testing.T, files *managedBuildTestFiles, runtimeRoot string, targets map[string]*managedRuntimeTarget) {
	t.Helper()
	pointer := managedPointer{}
	for component, target := range targets {
		value, err := json.Marshal(target)
		if err != nil {
			t.Fatal(err)
		}
		pointer[component] = value
	}
	pointer["status"], _ = json.Marshal("verified")
	value, err := json.MarshalIndent(pointer, "", "  ")
	if err != nil {
		t.Fatal(err)
	}
	files.put(filepath.Join(runtimeRoot, "active.json"), append(value, '\n'))
}

func writeManagedVerifyManifest(t *testing.T, files *managedBuildTestFiles, target *managedRuntimeTarget, identity managedRuntimeIdentity) {
	t.Helper()
	path, err := managedTargetIdentityPath(target)
	if err != nil {
		t.Fatal(err)
	}
	value, err := json.MarshalIndent(identity, "", "  ")
	if err != nil {
		t.Fatal(err)
	}
	files.put(path, append(value, '\n'))
}

func TestVerifyManagedActivatedTargetsVerifiesCanonicalSources(t *testing.T) {
	env, runtimeRoot, targets, _, _ := managedVerifyTestEnvironment(
		t,
		managedVerifyTestIdentity("server", "shared-source", "shared-tree", 5),
		managedVerifyTestIdentity("runner", "shared-source", "shared-tree", 5),
	)
	if err := verifyManagedActivatedTargets(context.Background(), env, runtimeRoot, []string{"server", "runner"}, targets); err != nil {
		t.Fatalf("verifyManagedActivatedTargets() error = %v", err)
	}
}

func TestVerifyManagedActivatedTargetsRejectsDivergence(t *testing.T) {
	tests := []struct {
		name    string
		mutate  func(t *testing.T, runtimeRoot string, targets map[string]*managedRuntimeTarget, observations map[string]managedRuntimeObservation, files *managedBuildTestFiles)
		wantErr string
	}{
		{
			name: "installed manifest",
			mutate: func(t *testing.T, _ string, targets map[string]*managedRuntimeTarget, _ map[string]managedRuntimeObservation, files *managedBuildTestFiles) {
				tampered := targets["server"].Identity
				tampered.TreeHash = strings.Repeat("f", 40)
				writeManagedVerifyManifest(t, files, targets["server"], tampered)
			},
			wantErr: "manifest.treeHash",
		},
		{
			name: "active pointer target",
			mutate: func(t *testing.T, runtimeRoot string, targets map[string]*managedRuntimeTarget, _ map[string]managedRuntimeObservation, files *managedBuildTestFiles) {
				tampered := *targets["server"]
				tampered.Identity.TreeHash = strings.Repeat("f", 40)
				writeManagedVerifyPointer(t, files, runtimeRoot, map[string]*managedRuntimeTarget{"server": &tampered})
			},
			wantErr: "pointer.treeHash",
		},
		{
			name: "server runtime",
			mutate: func(_ *testing.T, _ string, _ map[string]*managedRuntimeTarget, observations map[string]managedRuntimeObservation, _ *managedBuildTestFiles) {
				tampered := observations["server"]
				tampered.Identity.TreeHash = strings.Repeat("f", 40)
				observations["server"] = tampered
			},
			wantErr: "runtime.treeHash",
		},
		{
			name: "runner runtime",
			mutate: func(_ *testing.T, _ string, _ map[string]*managedRuntimeTarget, observations map[string]managedRuntimeObservation, _ *managedBuildTestFiles) {
				tampered := observations["runner"]
				tampered.Identity.TreeHash = strings.Repeat("f", 40)
				observations["runner"] = tampered
			},
			wantErr: "runtime.treeHash",
		},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			env, runtimeRoot, targets, observations, files := managedVerifyTestEnvironment(
				t,
				managedVerifyTestIdentity("server", "shared-source", "shared-tree", 5),
				managedVerifyTestIdentity("runner", "shared-source", "shared-tree", 5),
			)
			test.mutate(t, runtimeRoot, targets, observations, files)
			err := verifyManagedActivatedTargets(context.Background(), env, runtimeRoot, []string{"server", "runner"}, targets)
			if err == nil || !strings.Contains(err.Error(), test.wantErr) {
				t.Fatalf("error = %v, want %q", err, test.wantErr)
			}
			if strings.Contains(err.Error(), "unavailable") || strings.Contains(err.Error(), "not canonical") {
				t.Fatalf("error = %v, want a divergence rather than a read failure", err)
			}
		})
	}
}

func TestVerifyManagedActivatedTargetsRejectsCrossComponentDivergence(t *testing.T) {
	tests := []struct {
		name   string
		field  string
		mutate func(*managedRuntimeIdentity)
	}{
		{name: "source revision", field: "sourceRevision", mutate: func(identity *managedRuntimeIdentity) { identity.SourceRevision = "other-source" }},
		{name: "tree hash", field: "treeHash", mutate: func(identity *managedRuntimeIdentity) { identity.TreeHash = "other-tree" }},
		{name: "generation", field: "generation", mutate: func(identity *managedRuntimeIdentity) { identity.Generation = 6 }},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			runner := managedVerifyTestIdentity("runner", "shared-source", "shared-tree", 5)
			test.mutate(&runner)
			env, runtimeRoot, targets, _, _ := managedVerifyTestEnvironment(
				t,
				managedVerifyTestIdentity("server", "shared-source", "shared-tree", 5),
				runner,
			)
			err := verifyManagedActivatedTargets(context.Background(), env, runtimeRoot, []string{"server", "runner"}, targets)
			if err == nil || !strings.Contains(err.Error(), "identities disagree in "+test.field) {
				t.Fatalf("error = %v, want cross-component %q", err, test.field)
			}
		})
	}
}

func TestReadManagedReleaseIdentityFileReadsLegacyV0(t *testing.T) {
	files := newManagedBuildTestFiles()
	path := "/release/server/runtime-identity.json"
	files.put(path, []byte(`{
  "component": "server",
  "version": "0.0.0+legacy",
  "gitHash": "legacy-source",
  "treeHash": "legacy-tree",
  "artifactDigest": "legacy-digest",
  "releaseId": "mohist-server-legacy",
  "generation": 3
}`))
	identity, legacy, err := readManagedReleaseIdentityFile(files, path, "server")
	if err != nil {
		t.Fatalf("readManagedReleaseIdentityFile() error = %v", err)
	}
	if !legacy {
		t.Fatal("readManagedReleaseIdentityFile() did not report the legacy v0 payload")
	}
	if identity.SchemaVersion != 0 || identity.SourceRevision != "legacy-source" || identity.BuildGitHash != "legacy-source" {
		t.Fatalf("legacy identity = %#v", identity)
	}
}

func TestVerifyManagedActivatedTargetsReadsLegacyManifestButComparesCanonically(t *testing.T) {
	env, runtimeRoot, targets, _, files := managedVerifyTestEnvironment(
		t,
		managedVerifyTestIdentity("server", "shared-source", "shared-tree", 5),
		managedVerifyTestIdentity("runner", "shared-source", "shared-tree", 5),
	)
	legacy := map[string]any{
		"component": targets["server"].Identity.Component, "version": targets["server"].Identity.Version,
		"sourceRevision": targets["server"].Identity.SourceRevision, "buildGitHash": targets["server"].Identity.BuildGitHash,
		"treeHash": targets["server"].Identity.TreeHash, "artifactDigest": targets["server"].Identity.ArtifactDigest,
		"releaseId": targets["server"].Identity.ReleaseID, "generation": targets["server"].Identity.Generation,
	}
	identityPath, err := managedTargetIdentityPath(targets["server"])
	if err != nil {
		t.Fatal(err)
	}
	value, err := json.MarshalIndent(legacy, "", "  ")
	if err != nil {
		t.Fatal(err)
	}
	files.put(identityPath, append(value, '\n'))

	err = verifyManagedActivatedTargets(context.Background(), env, runtimeRoot, []string{"server"}, targets)
	if err == nil || !strings.Contains(err.Error(), "manifest.schemaVersion") {
		t.Fatalf("error = %v, want canonical schemaVersion divergence for the activated candidate", err)
	}
	if strings.Contains(err.Error(), "unavailable") || strings.Contains(err.Error(), "not canonical") {
		t.Fatalf("error = %v, want the legacy manifest to stay readable", err)
	}
}

func assertManagedUpdateRolledBack(t *testing.T, fixture *managedUpdateFixture, originalUnit, originalActive, originalVerified string) {
	t.Helper()
	if fixture.files.text(fixture.unitPath) != originalUnit {
		t.Fatal("rollback did not restore the exact service unit")
	}
	if fixture.files.text(filepath.Join(fixture.runtimeRoot, "active.json")) != originalActive {
		t.Fatal("rollback did not restore the exact active pointer")
	}
	if fixture.files.text(filepath.Join(fixture.runtimeRoot, "verified.json")) != originalVerified {
		t.Fatal("rollback did not restore the exact verified pointer")
	}
	if fixture.latestTransaction().Status != "rolled-back" {
		t.Fatalf("transaction = %#v", fixture.latestTransaction())
	}
	if fixture.files.Exists(filepath.Join(fixture.runtimeRoot, "pending.json")) {
		t.Fatal("rolled-back verification left a pending marker")
	}
}

func TestManagedUpdateRollsBackOnActivatedServerIdentityDivergence(t *testing.T) {
	tests := []struct {
		name    string
		setup   func(*managedUpdateFixture)
		wantErr string
	}{
		{
			name: "installed manifest",
			setup: func(fixture *managedUpdateFixture) {
				fixture.files.mutateIdentityRead = func(path string, value []byte) []byte {
					if !strings.Contains(path, managedTestCommit+"-g8/server/runtime-identity.json") {
						return value
					}
					var identity managedRuntimeIdentity
					if json.Unmarshal(value, &identity) != nil {
						return value
					}
					identity.TreeHash = strings.Repeat("f", 40)
					encoded, err := json.MarshalIndent(identity, "", "  ")
					if err != nil {
						return value
					}
					return append(encoded, '\n')
				}
			},
			wantErr: "activated identity differs",
		},
		{
			name:    "active pointer target",
			setup:   func(fixture *managedUpdateFixture) { fixture.files.mutateActivePointer = true },
			wantErr: "pointer.treeHash",
		},
		{
			name:    "server runtime",
			setup:   func(fixture *managedUpdateFixture) { fixture.control.serverVerificationMismatch = true },
			wantErr: "runtime.treeHash",
		},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			fixture := newManagedUpdateFixture(t)
			test.setup(fixture)
			originalUnit := fixture.files.text(fixture.unitPath)
			originalActive := fixture.files.text(filepath.Join(fixture.runtimeRoot, "active.json"))
			originalVerified := fixture.files.text(filepath.Join(fixture.runtimeRoot, "verified.json"))

			err := fixture.updater.Update(context.Background(), ManagedUpdateRequest{
				Components: []string{"server"}, RepoRoot: "/repo",
			})
			if err == nil || !strings.Contains(err.Error(), test.wantErr) {
				t.Fatalf("error = %v, want %q", err, test.wantErr)
			}
			assertManagedUpdateRolledBack(t, fixture, originalUnit, originalActive, originalVerified)
		})
	}
}

func TestManagedUpdateRollsBackOnActivatedRunnerIdentityDivergence(t *testing.T) {
	fixture := newManagedRunnerUpdateFixture(t)
	fixture.control.runnerVerificationMismatch = true
	originalUnit := fixture.files.text(fixture.unitPath)
	originalActive := fixture.files.text(filepath.Join(fixture.runtimeRoot, "active.json"))
	originalVerified := fixture.files.text(filepath.Join(fixture.runtimeRoot, "verified.json"))

	err := fixture.updater.Update(context.Background(), ManagedUpdateRequest{
		Components: []string{"runner"}, RepoRoot: "/repo",
	})
	if err == nil || !strings.Contains(err.Error(), "runtime.treeHash") {
		t.Fatalf("error = %v, want the activated Runner identity divergence", err)
	}
	assertManagedUpdateRolledBack(t, fixture, originalUnit, originalActive, originalVerified)
}

func TestVerifyManagedActivatedTargetsReadsLegacyPointerButComparesCanonically(t *testing.T) {
	env, runtimeRoot, targets, _, files := managedVerifyTestEnvironment(
		t,
		managedVerifyTestIdentity("server", "shared-source", "shared-tree", 5),
		managedVerifyTestIdentity("runner", "shared-source", "shared-tree", 5),
	)
	identity := targets["server"].Identity
	legacyTarget := map[string]any{
		"component": "server",
		"identity": map[string]any{
			"component": "server", "version": identity.Version,
			"sourceRevision": identity.SourceRevision, "buildGitHash": identity.BuildGitHash,
			"treeHash": identity.TreeHash, "artifactDigest": identity.ArtifactDigest,
			"releaseId": identity.ReleaseID, "generation": identity.Generation,
		},
	}
	pointer := managedPointer{}
	value, err := json.Marshal(legacyTarget)
	if err != nil {
		t.Fatal(err)
	}
	pointer["server"] = value
	pointer["status"], _ = json.Marshal("verified")
	encoded, err := json.MarshalIndent(pointer, "", "  ")
	if err != nil {
		t.Fatal(err)
	}
	files.put(filepath.Join(runtimeRoot, "active.json"), append(encoded, '\n'))

	err = verifyManagedActivatedTargets(context.Background(), env, runtimeRoot, []string{"server"}, targets)
	if err == nil || !strings.Contains(err.Error(), "pointer.schemaVersion") {
		t.Fatalf("error = %v, want canonical schemaVersion divergence for the activated candidate", err)
	}
	if strings.Contains(err.Error(), "unavailable") || strings.Contains(err.Error(), "invalid") {
		t.Fatalf("error = %v, want the legacy pointer target to stay readable", err)
	}
}
