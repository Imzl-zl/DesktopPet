using System.IO.Compression;
using System.Text;

namespace DesktopPet.Infra.Diagnostics;

public sealed class DiagnosticExporter
{
    private readonly string _logsDirectory;
    private readonly Action _flushLogs;

    public DiagnosticExporter(string logsDirectory, Action flushLogs)
    {
        _logsDirectory = logsDirectory;
        _flushLogs = flushLogs;
    }

    public void Export(string destinationZip)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZip);
        _flushLogs();
        var parent = Path.GetDirectoryName(Path.GetFullPath(destinationZip));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        var temporary = destinationZip + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                if (Directory.Exists(_logsDirectory))
                {
                    foreach (var file in Directory.EnumerateFiles(_logsDirectory, "*.log*").ToArray())
                    {
                        FileStream input;
                        try
                        {
                            input = new FileStream(
                                file,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.ReadWrite | FileShare.Delete);
                        }
                        catch (FileNotFoundException)
                        {
                            continue; // Logger rotated this snapshot between enumeration and open.
                        }
                        using (input)
                        {
                            var entry = archive.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
                            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                            using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                            writer.Write(SecretRedactor.Redact(reader.ReadToEnd()));
                        }
                    }
                }
            }
            File.Move(temporary, destinationZip, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (FileNotFoundException) { }
        }
    }
}
