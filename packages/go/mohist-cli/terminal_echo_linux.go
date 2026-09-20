//go:build linux

package mohistcli

import (
	"syscall"
	"unsafe"
)

// termiosState is the terminal state captured before echo is disabled.
type termiosState struct{ syscall.Termios }

func terminalEchoAvailable() bool { return true }

// disableTerminalEcho clears the ECHO flag on the terminal behind fd and
// returns the previous state so the caller restores it.
func disableTerminalEcho(fd uintptr) (termiosState, bool) {
	var state termiosState
	if _, _, errno := syscall.Syscall(
		syscall.SYS_IOCTL, fd, syscall.TCGETS, uintptr(unsafe.Pointer(&state.Termios))); errno != 0 {
		return state, false
	}
	previous := state
	state.Lflag &^= syscall.ECHO
	if _, _, errno := syscall.Syscall(
		syscall.SYS_IOCTL, fd, syscall.TCSETS, uintptr(unsafe.Pointer(&state.Termios))); errno != 0 {
		return previous, false
	}
	return previous, true
}

func restoreTerminalEcho(fd uintptr, state termiosState) {
	_, _, _ = syscall.Syscall(syscall.SYS_IOCTL, fd, syscall.TCSETS, uintptr(unsafe.Pointer(&state.Termios)))
}
