using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Mohist.Server.Infrastructure.Events.Matching;

internal interface IBooleanMatchNode
{
    bool Evaluate(EventMatchInput input);
}

internal interface IStringMatchNode
{
    string Evaluate(EventMatchInput input);
}

internal sealed record AttributeMatchNode(string Name) : IStringMatchNode
{
    public string Evaluate(EventMatchInput input) => input.GetValue(Name);
}

internal sealed record StringMatchNode(string Value) : IStringMatchNode
{
    public string Evaluate(EventMatchInput input) => Value;
}

internal sealed record OrMatchNode(IBooleanMatchNode Left, IBooleanMatchNode Right) : IBooleanMatchNode
{
    public bool Evaluate(EventMatchInput input) => Left.Evaluate(input) || Right.Evaluate(input);
}

internal sealed record AndMatchNode(IBooleanMatchNode Left, IBooleanMatchNode Right) : IBooleanMatchNode
{
    public bool Evaluate(EventMatchInput input) => Left.Evaluate(input) && Right.Evaluate(input);
}

internal sealed record NotMatchNode(IBooleanMatchNode Inner) : IBooleanMatchNode
{
    public bool Evaluate(EventMatchInput input) => !Inner.Evaluate(input);
}

internal sealed record EqualityMatchNode(IStringMatchNode Left, IStringMatchNode Right, bool Negated) : IBooleanMatchNode
{
    public bool Evaluate(EventMatchInput input)
    {
        var equal = string.Equals(Left.Evaluate(input), Right.Evaluate(input), StringComparison.Ordinal);
        return Negated ? !equal : equal;
    }
}

internal sealed record MembershipMatchNode(AttributeMatchNode Attribute, ImmutableHashSet<string> Values) : IBooleanMatchNode
{
    public bool Evaluate(EventMatchInput input) => Values.Contains(Attribute.Evaluate(input));
}

internal sealed record PresenceMatchNode(string Attribute) : IBooleanMatchNode
{
    public bool Evaluate(EventMatchInput input) => input.Has(Attribute);
}

internal enum StringFunction
{
    StartsWith,
    EndsWith,
    Contains,
}

internal sealed record StringFunctionMatchNode(AttributeMatchNode Attribute, StringFunction Function, string Argument) : IBooleanMatchNode
{
    public bool Evaluate(EventMatchInput input)
    {
        var value = Attribute.Evaluate(input);
        return Function switch
        {
            StringFunction.StartsWith => value.StartsWith(Argument, StringComparison.Ordinal),
            StringFunction.EndsWith => value.EndsWith(Argument, StringComparison.Ordinal),
            StringFunction.Contains => value.Contains(Argument, StringComparison.Ordinal),
            _ => false,
        };
    }
}

internal sealed record RegexMatchNode(AttributeMatchNode Attribute, Regex Pattern) : IBooleanMatchNode
{
    public bool Evaluate(EventMatchInput input)
    {
        try
        {
            return Pattern.IsMatch(Attribute.Evaluate(input));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
