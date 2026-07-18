using System.Collections.Immutable;

namespace Mohist.Server.Infrastructure.Events.Matching;

internal enum MatchTokenKind
{
    End,
    Identifier,
    String,
    LeftParenthesis,
    RightParenthesis,
    LeftBracket,
    RightBracket,
    Dot,
    Comma,
    Equal,
    NotEqual,
    And,
    Or,
    Not,
}

internal readonly record struct MatchToken(MatchTokenKind Kind, string Text, int Offset, int Line, int Column);

internal sealed class MatchTokenizer
{
    private readonly string _source;
    private int _offset;
    private int _line = 1;
    private int _column = 1;

    public MatchTokenizer(string source)
    {
        _source = source;
    }

    public ImmutableArray<MatchToken> Tokenize()
    {
        var tokens = ImmutableArray.CreateBuilder<MatchToken>();
        while (true)
        {
            SkipWhitespace();
            if (_offset == _source.Length)
            {
                tokens.Add(new MatchToken(MatchTokenKind.End, string.Empty, _offset, _line, _column));
                return tokens.ToImmutable();
            }

            var offset = _offset;
            var line = _line;
            var column = _column;
            var current = Current;
            switch (current)
            {
                case '(':
                    tokens.Add(Single(MatchTokenKind.LeftParenthesis, offset, line, column));
                    break;
                case ')':
                    tokens.Add(Single(MatchTokenKind.RightParenthesis, offset, line, column));
                    break;
                case '[':
                    tokens.Add(Single(MatchTokenKind.LeftBracket, offset, line, column));
                    break;
                case ']':
                    tokens.Add(Single(MatchTokenKind.RightBracket, offset, line, column));
                    break;
                case '.':
                    tokens.Add(Single(MatchTokenKind.Dot, offset, line, column));
                    break;
                case ',':
                    tokens.Add(Single(MatchTokenKind.Comma, offset, line, column));
                    break;
                case '=' when Peek(1) == '=':
                    tokens.Add(Double(MatchTokenKind.Equal, offset, line, column));
                    break;
                case '!' when Peek(1) == '=':
                    tokens.Add(Double(MatchTokenKind.NotEqual, offset, line, column));
                    break;
                case '&' when Peek(1) == '&':
                    tokens.Add(Double(MatchTokenKind.And, offset, line, column));
                    break;
                case '|' when Peek(1) == '|':
                    tokens.Add(Double(MatchTokenKind.Or, offset, line, column));
                    break;
                case '!':
                    tokens.Add(Single(MatchTokenKind.Not, offset, line, column));
                    break;
                case '"':
                    tokens.Add(ReadString(offset, line, column));
                    break;
                default:
                    if (IsIdentifierStart(current))
                    {
                        tokens.Add(ReadIdentifier(offset, line, column));
                        break;
                    }

                    throw Error($"Unexpected character '{current}'.", offset, line, column);
            }
        }
    }

    private MatchToken ReadString(int offset, int line, int column)
    {
        Advance();
        var value = new System.Text.StringBuilder();
        while (_offset < _source.Length)
        {
            var current = Current;
            if (current == '"')
            {
                Advance();
                return new MatchToken(MatchTokenKind.String, value.ToString(), offset, line, column);
            }

            if (current == '\n' || current == '\r')
                throw Error("String literal cannot contain a line break.", _offset, _line, _column);

            if (current == '\\')
            {
                var escapeOffset = _offset;
                var escapeLine = _line;
                var escapeColumn = _column;
                Advance();
                if (_offset == _source.Length)
                    throw Error("Unterminated string literal.", offset, line, column);

                value.Append(Current switch
                {
                    '"' => '"',
                    '\\' => '\\',
                    '/' => '/',
                    'b' => '\b',
                    'f' => '\f',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => throw Error("Invalid string escape.", escapeOffset, escapeLine, escapeColumn),
                });
                Advance();
                continue;
            }

            value.Append(current);
            Advance();
        }

        throw Error("Unterminated string literal.", offset, line, column);
    }

    private MatchToken ReadIdentifier(int offset, int line, int column)
    {
        while (_offset < _source.Length && IsIdentifierPart(Current))
            Advance();
        return new MatchToken(MatchTokenKind.Identifier, _source[offset.._offset], offset, line, column);
    }

    private MatchToken Single(MatchTokenKind kind, int offset, int line, int column)
    {
        var text = Current.ToString();
        Advance();
        return new MatchToken(kind, text, offset, line, column);
    }

    private MatchToken Double(MatchTokenKind kind, int offset, int line, int column)
    {
        var text = _source.Substring(_offset, 2);
        Advance();
        Advance();
        return new MatchToken(kind, text, offset, line, column);
    }

    private void SkipWhitespace()
    {
        while (_offset < _source.Length && char.IsWhiteSpace(Current))
            Advance();
    }

    private void Advance()
    {
        if (Current == '\n')
        {
            _line++;
            _column = 1;
        }
        else
        {
            _column++;
        }
        _offset++;
    }

    private char Current => _source[_offset];

    private char Peek(int distance) => _offset + distance < _source.Length ? _source[_offset + distance] : '\0';

    private static bool IsIdentifierStart(char value) => value == '_' || char.IsLetter(value);

    private static bool IsIdentifierPart(char value) => value == '_' || char.IsLetterOrDigit(value);

    private static MatchParseException Error(string message, int offset, int line, int column) =>
        new(new MatchDiagnostic(message, offset, line, column));
}

internal sealed class MatchParseException : Exception
{
    public MatchParseException(MatchDiagnostic diagnostic)
        : base(diagnostic.Message)
    {
        Diagnostic = diagnostic;
    }

    public MatchDiagnostic Diagnostic { get; }
}
