//go:build !linux

package mohistcli

// termiosState is empty where the standard library exposes no termios binding.
// The guide refuses to read a secret with echo left on and names the protected
// file instead.
type termiosState struct{}

func terminalEchoAvailable() bool { return false }

func disableTerminalEcho(uintptr) (termiosState, bool) { return termiosState{}, false }

func restoreTerminalEcho(uintptr, termiosState) {}
