namespace Mohist.Server.Workflow.Storage;

internal static class WorkflowArtifactStreamCopier
{
    public static async Task<long> CopyAsync(
        Stream source,
        Stream destination,
        long declaredSize,
        long? maxBytes,
        string displayPath,
        CancellationToken cancellationToken)
    {
        if (declaredSize < 0)
            throw new WorkflowArtifactStorageException(
                $"Declared size {declaredSize} is negative.");

        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (maxBytes is { } maximum && written + read > maximum)
                throw new WorkflowArtifactStorageException(
                    $"Content for '{displayPath}' exceeds size limit ({maximum} bytes).");

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            written += read;
        }

        if (written != declaredSize)
            throw new WorkflowArtifactStorageException(
                $"Content size mismatch for '{displayPath}': declared {declaredSize} bytes, wrote {written} bytes.");

        return written;
    }
}
