namespace Mohist.Server.Epic.Grains;

public interface IEpicCounterGrain : IGrainWithStringKey
{
    Task<int> NextAsync();
}
