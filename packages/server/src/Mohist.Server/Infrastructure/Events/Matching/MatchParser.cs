using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Mohist.Server.Infrastructure.Events.Matching;

internal sealed class MatchParser
{
    private readonly ImmutableArray<MatchToken> _tokens;
    private readonly TimeSpan _regexTimeout;
    private int _position;

    public MatchParser(ImmutableArray<MatchToken> tokens, TimeSpan regexTimeout)
    {
        _tokens = tokens;
        _regexTimeout = regexTimeout;
    }

    public IBooleanMatchNode Parse()
    {
        var expression = ParseOr();
        Expect(MatchTokenKind.End, "Unexpected token after expression.");
        return expression;
    }

    private IBooleanMatchNode ParseOr()
    {
        var expression = ParseAnd();
        while (Match(MatchTokenKind.Or))
            expression = new OrMatchNode(expression, ParseAnd());
        return expression;
    }

    private IBooleanMatchNode ParseAnd()
    {
        var expression = ParseUnary();
        while (Match(MatchTokenKind.And))
            expression = new AndMatchNode(expression, ParseUnary());
        return expression;
    }

    private IBooleanMatchNode ParseUnary()
    {
        if (Match(MatchTokenKind.Not))
            return new NotMatchNode(ParseUnary());
        return ParsePrimary();
    }

    private IBooleanMatchNode ParsePrimary()
    {
        if (Match(MatchTokenKind.LeftParenthesis))
        {
            var expression = ParseOr();
            Expect(MatchTokenKind.RightParenthesis, "Expected ')'.");
            return expression;
        }

        if (Current.Kind == MatchTokenKind.Identifier && Current.Text == "has")
            return ParsePresence();

        return ParseAttributeExpression();
    }

    private IBooleanMatchNode ParsePresence()
    {
        Advance();
        Expect(MatchTokenKind.LeftParenthesis, "Expected '(' after has.");
        var attribute = ParseAttribute();
        Expect(MatchTokenKind.RightParenthesis, "Expected ')' after has argument.");
        return new PresenceMatchNode(attribute.Name);
    }

    private IBooleanMatchNode ParseAttributeExpression()
    {
        var left = ParseOperand();
        if (Match(MatchTokenKind.Equal))
            return new EqualityMatchNode(left, ParseOperand(), false);
        if (Match(MatchTokenKind.NotEqual))
            return new EqualityMatchNode(left, ParseOperand(), true);

        if (left is not AttributeMatchNode attribute)
            throw Error("Expected comparison operator.", Current);

        if (Current.Kind == MatchTokenKind.Identifier && Current.Text == "in")
        {
            Advance();
            return ParseMembership(attribute);
        }

        if (Match(MatchTokenKind.Dot))
            return ParseFunction(attribute);

        throw Error("Expected comparison, membership, or string function.", Current);
    }

    private IBooleanMatchNode ParseMembership(AttributeMatchNode attribute)
    {
        Expect(MatchTokenKind.LeftBracket, "Expected '[' after in.");
        var values = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        if (!Match(MatchTokenKind.RightBracket))
        {
            values.Add(Expect(MatchTokenKind.String, "Expected a double-quoted string in list.").Text);
            while (Match(MatchTokenKind.Comma))
                values.Add(Expect(MatchTokenKind.String, "Expected a double-quoted string after ','.").Text);
            Expect(MatchTokenKind.RightBracket, "Expected ']'.");
        }
        return new MembershipMatchNode(attribute, values.ToImmutable());
    }

    private IBooleanMatchNode ParseFunction(AttributeMatchNode attribute)
    {
        var function = Expect(MatchTokenKind.Identifier, "Expected string function name.");
        Expect(MatchTokenKind.LeftParenthesis, "Expected '(' after string function.");
        var argument = Expect(MatchTokenKind.String, "Expected a double-quoted string argument.");
        Expect(MatchTokenKind.RightParenthesis, "Expected ')' after string function argument.");

        return function.Text switch
        {
            "startsWith" => new StringFunctionMatchNode(attribute, StringFunction.StartsWith, argument.Text),
            "endsWith" => new StringFunctionMatchNode(attribute, StringFunction.EndsWith, argument.Text),
            "contains" => new StringFunctionMatchNode(attribute, StringFunction.Contains, argument.Text),
            "matches" => CompileRegex(attribute, argument),
            _ => throw Error($"Unknown string function '{function.Text}'.", function),
        };
    }

    private RegexMatchNode CompileRegex(AttributeMatchNode attribute, MatchToken argument)
    {
        try
        {
            return new RegexMatchNode(attribute, new Regex(argument.Text, RegexOptions.CultureInvariant, _regexTimeout));
        }
        catch (ArgumentException exception)
        {
            throw Error($"Invalid regular expression: {exception.Message}", argument);
        }
    }

    private IStringMatchNode ParseOperand()
    {
        if (Current.Kind == MatchTokenKind.String)
        {
            var value = Current.Text;
            Advance();
            return new StringMatchNode(value);
        }
        return ParseAttribute();
    }

    private AttributeMatchNode ParseAttribute()
    {
        var root = Expect(MatchTokenKind.Identifier, "Expected event attribute.");
        if (root.Text != "event")
            throw Error("Expected event attribute.", root);
        Expect(MatchTokenKind.Dot, "Expected '.' after event.");
        var attribute = Expect(MatchTokenKind.Identifier, "Expected attribute name.");
        if (attribute.Text == "data")
            throw Error("event.data is not addressable.", attribute);
        return new AttributeMatchNode(attribute.Text);
    }

    private bool Match(MatchTokenKind kind)
    {
        if (Current.Kind != kind)
            return false;
        Advance();
        return true;
    }

    private MatchToken Expect(MatchTokenKind kind, string message)
    {
        if (Current.Kind != kind)
            throw Error(message, Current);
        var token = Current;
        Advance();
        return token;
    }

    private MatchToken Current => _tokens[_position];

    private void Advance()
    {
        if (_position < _tokens.Length - 1)
            _position++;
    }

    private static MatchParseException Error(string message, MatchToken token) =>
        new(new MatchDiagnostic(message, token.Offset, token.Line, token.Column));
}
