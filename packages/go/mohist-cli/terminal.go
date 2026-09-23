package mohistcli

import (
	"errors"
	"fmt"
	"io"
	"os"
	"strings"
)

// A guide prompts only when a person is present: a piped caller, a structured
// output call, and a CI run must never be blocked by a terminal.
func defaultTerminalInteractive() bool {
	if disabled, ok := os.LookupEnv("MOHIST_PROMPT_DISABLED"); ok && disabled != "" && disabled != "0" {
		return false
	}
	// A CI system can allocate a pseudo terminal, so the conventional marker is
	// honored beside the character-device check.
	if marker, ok := os.LookupEnv("CI"); ok && marker != "" && marker != "0" {
		return false
	}
	info, err := os.Stdin.Stat()
	if err != nil {
		return false
	}
	return info.Mode()&os.ModeCharDevice != 0
}

// defaultReadSecretLine reads one credential value with terminal echo disabled,
// so a typed secret is never displayed.
func defaultReadSecretLine(prompt string) (string, error) {
	if !terminalEchoAvailable() {
		return "", errors.New("hidden terminal input is unavailable on this platform; pass --credentials-file <path>")
	}
	fmt.Fprint(os.Stderr, prompt)
	state, echoOff := disableTerminalEcho(os.Stdin.Fd())
	line, err := readTerminalLine(os.Stdin)
	if echoOff {
		restoreTerminalEcho(os.Stdin.Fd(), state)
	}
	fmt.Fprintln(os.Stderr)
	if err != nil {
		return "", err
	}
	return strings.TrimSpace(line), nil
}

// readTerminalLine reads one byte at a time so a pasted second line stays in
// the stream for the next prompt of the same step.
func readTerminalLine(file *os.File) (string, error) {
	var line []byte
	buffer := make([]byte, 1)
	for {
		count, err := file.Read(buffer)
		if count > 0 {
			if buffer[0] == '\n' {
				return string(line), nil
			}
			line = append(line, buffer[0])
		}
		if err != nil {
			if errors.Is(err, io.EOF) {
				return string(line), nil
			}
			return "", err
		}
	}
}
