namespace Mohist.Server.Epic.Grains;

public interface IEpicCounterGrain : IGrainWithStringKey
{
    Task<int> NextAsync();
}

[GenerateSerializer]
public sealed record EpicCounterState([property: Id(0)] int Next);
