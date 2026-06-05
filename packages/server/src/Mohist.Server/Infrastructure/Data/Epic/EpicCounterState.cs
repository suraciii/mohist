namespace Mohist.Server.Infrastructure.Data.Epic;

[GenerateSerializer]
public sealed record EpicCounterState([property: Id(0)] int Next);
