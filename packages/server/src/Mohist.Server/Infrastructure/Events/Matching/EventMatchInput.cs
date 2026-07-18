namespace Mohist.Server.Infrastructure.Events.Matching;

public interface EventMatchInput
{
    string GetValue(string attribute);

    bool Has(string attribute);
}
