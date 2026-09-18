package mohistcli

import (
	"encoding/json"
	"path/filepath"
	"strings"
	"testing"
)

func managedPreflightTestTarget(component string) *managedRuntimeTarget {
	identity := managedRuntimeIdentity{
		SchemaVersion: 1, Component: component, Version: "0.0.0+" + managedBuildTestCommit,
		SourceRevision: managedBuildTestCommit, BuildGitHash: managedBuildTestCommit,
		TreeHash: managedBuildTestTree, ArtifactDigest: strings.Repeat("a", 64),
		ReleaseID: "mohist-" + component + "-" + managedBuildTestCommit, Generation: 9,
	}
	if component == "runner" {
		identity.RunnerID = "runner-1"
	}
	root := filepath.Join("/managed/candidate", component)
	return &managedRuntimeTarget{
		Component: component, WorkingDirectory: root, Entrypoint: filepath.Join(root, "entry"),
		Identity: identity, IsAbsoluteTarget: true, UsesCanonicalEntrypoint: true,
	}
}

func managedPreflightTestIdentityJSON(t *testing.T, identity managedRuntimeIdentity) []byte {
	t.Helper()
	value, err := json.MarshalIndent(identity, "", "  ")
	if err != nil {
		t.Fatal(err)
	}
	return append(value, '\n')
}

func TestValidateManagedStagedCandidateRejectsMalformedAndMissingManifest(t *testing.T) {
	target := managedPreflightTestTarget("server")
	source := managedSource{Commit: managedBuildTestCommit, TreeHash: managedBuildTestTree}
	tests := []struct {
		name    string
		value   []byte
		present bool
		wantErr string
	}{
		{name: "canonical", value: managedPreflightTestIdentityJSON(t, target.Identity), present: true},
		{name: "missing", present: false, wantErr: "unavailable"},
		{name: "malformed json", value: []byte("{not-json"), present: true, wantErr: "invalid"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			files := newManagedBuildTestFiles()
			if test.present {
				files.put(filepath.Join(target.WorkingDirectory, "runtime-identity.json"), test.value)
			}
			err := validateManagedStagedCandidate(files, target.WorkingDirectory, target, source, target.Identity.Generation)
			if test.wantErr == "" {
				if err != nil {
					t.Fatalf("validateManagedStagedCandidate() error = %v", err)
				}
				return
			}
			if err == nil || !strings.Contains(err.Error(), test.wantErr) {
				t.Fatalf("error = %v, want %q", err, test.wantErr)
			}
		})
	}
}

func TestValidateManagedInstalledReleaseManifestRejectsMalformedAndMismatch(t *testing.T) {
	target := managedPreflightTestTarget("server")
	path := filepath.Join(target.WorkingDirectory, "runtime-identity.json")
	tests := []struct {
		name    string
		value   []byte
		present bool
		wantErr string
	}{
		{name: "canonical", value: managedPreflightTestIdentityJSON(t, target.Identity), present: true},
		{name: "missing", present: false, wantErr: "unavailable"},
		{name: "malformed json", value: []byte("{not-json"), present: true, wantErr: "invalid"},
		{
			name: "bad schema version",
			value: func() []byte {
				identity := target.Identity
				identity.SchemaVersion = 3
				return managedPreflightTestIdentityJSON(t, identity)
			}(),
			present: true, wantErr: "not canonical",
		},
		{
			name: "identity disagreement",
			value: func() []byte {
				identity := target.Identity
				identity.ReleaseID = "mohist-other"
				return managedPreflightTestIdentityJSON(t, identity)
			}(),
			present: true, wantErr: "release manifest does not match its pointer target",
		},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			files := newManagedBuildTestFiles()
			if test.present {
				files.put(path, test.value)
			}
			err := validateManagedInstalledReleaseManifest(files, target)
			if test.wantErr == "" {
				if err != nil {
					t.Fatalf("validateManagedInstalledReleaseManifest() error = %v", err)
				}
				return
			}
			if err == nil || !strings.Contains(err.Error(), test.wantErr) {
				t.Fatalf("error = %v, want %q", err, test.wantErr)
			}
		})
	}
}

func TestValidateManagedLiveIdentityRejectsMalformedAndMismatch(t *testing.T) {
	target := managedPreflightTestTarget("server")
	tests := []struct {
		name    string
		mutate  func(*managedRuntimeObservation)
		wantErr string
	}{
		{name: "matching", mutate: func(*managedRuntimeObservation) {}},
		{
			name:    "malformed identity",
			mutate:  func(observation *managedRuntimeObservation) { observation.Identity.SchemaVersion = 0 },
			wantErr: "not canonical",
		},
		{
			name: "source mismatch",
			mutate: func(observation *managedRuntimeObservation) {
				observation.Identity.SourceRevision = strings.Repeat("f", 40)
			},
			wantErr: "sourceRevision",
		},
		{
			name: "build git hash mismatch",
			mutate: func(observation *managedRuntimeObservation) {
				observation.Identity.BuildGitHash = strings.Repeat("f", 40)
			},
			wantErr: "buildGitHash",
		},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			observation := managedRuntimeObservation{Identity: target.Identity, Status: "ok"}
			test.mutate(&observation)
			err := validateManagedLiveIdentity("server", observation, target.Identity)
			if test.wantErr == "" {
				if err != nil {
					t.Fatalf("validateManagedLiveIdentity() error = %v", err)
				}
				return
			}
			if err == nil || !strings.Contains(err.Error(), test.wantErr) {
				t.Fatalf("error = %v, want %q", err, test.wantErr)
			}
		})
	}
}
