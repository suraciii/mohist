package mohistslack

import (
	"io"
	"log/slog"
	"strings"
)

// NewLogger assembles the process logger per design/slack-go-port.md:
// stderr text lines by default, JSON lines when format is "json". The Node
// implementation's interactive-terminal colorization is dropped on purpose —
// the port adds no dependencies beyond slack-go.
func NewLogger(stderr io.Writer, format string) *slog.Logger {
	if strings.EqualFold(strings.TrimSpace(format), "json") {
		return slog.New(slog.NewJSONHandler(stderr, nil))
	}
	return slog.New(slog.NewTextHandler(stderr, nil))
}
