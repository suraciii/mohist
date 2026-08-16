using Mohist.Server.Runner.Grains;

namespace Mohist.Server.TestSupport;

public sealed class RunnerUpdateOperationWriteFailureProbe : IRunnerUpdateOperationWriteFailureInjector
{
    private int _nextFailureKind = -1;

    public void FailNext(RunnerUpdateOperationWriteKind kind) =>
        Interlocked.Exchange(ref _nextFailureKind, (int)kind);

    public void BeforeWrite(
        RunnerUpdateOperationWriteKind kind,
        string operationId,
        string ownerKind,
        string ownerId,
        string workId)
    {
        if (Interlocked.CompareExchange(ref _nextFailureKind, -1, (int)kind) == (int)kind)
        {
            throw new InvalidOperationException(
                $"Injected update-operation {kind} write failure for '{operationId}/{ownerKind}/{ownerId}/{workId}'.");
        }
    }
}
