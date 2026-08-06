using System.Runtime.ExceptionServices;
using System.Text;

namespace DesktopPet.Infra.Diagnostics;

public static class AtomicFileWriter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static void WriteAllText(string destinationPath, string content)
        => Write(destinationPath, stream =>
        {
            using var writer = new StreamWriter(stream, Utf8WithoutBom, bufferSize: 4096, leaveOpen: true);
            writer.Write(content);
            writer.Flush();
        });

    public static void WriteAllBytes(string destinationPath, byte[] content)
        => Write(destinationPath, stream => stream.Write(content));

    private static void Write(string destinationPath, Action<FileStream> write)
    {
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Destination must have a parent directory", nameof(destinationPath));
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        Exception? failure = null;
        var published = false;
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough | FileOptions.SequentialScan))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
            published = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = ex;
        }

        if (!published)
        {
            try { File.Delete(temporaryPath); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                failure = failure is null ? cleanupError : new AggregateException(failure, cleanupError);
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
