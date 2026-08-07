using System.IO.Compression;
using DesktopPet.Infra.Diagnostics;

namespace DesktopPet.Infra.Tests;

public sealed class DiagnosticsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "DesktopPet.Diagnostics.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Authorization: Bearer top-secret", "top-secret")]
    [InlineData("apiKey=secret-value", "secret-value")]
    [InlineData("https://host/v1?access_token=query-secret", "query-secret")]
    [InlineData("key sk-1234567890abcdef", "sk-1234567890abcdef")]
    [InlineData("client_secret=client-secret-value", "client-secret-value")]
    [InlineData("\"Authorization\": \"Bearer json-secret-value\"", "json-secret-value")]
    [InlineData("Authorization: Basic YmFzaWMtc2VjcmV0", "YmFzaWMtc2VjcmV0")]
    [InlineData("Authorization: ApiKey short-secret-value", "short-secret-value")]
    [InlineData("\"Authorization\": \"Custom short json secret\"", "short json secret")]
    [InlineData("credential=custom-provider-secret", "custom-provider-secret")]
    [InlineData("opaque abcdefghijklmnopqrstuvwxyz1234567890", "abcdefghijklmnopqrstuvwxyz1234567890")]
    public void Redactor_RemovesCommonSecretShapes(string input, string secret)
    {
        var output = SecretRedactor.Redact(input);
        Assert.DoesNotContain(secret, output);
        Assert.Contains(SecretRedactor.Replacement, output);
    }

    [Fact]
    public void RollingLogger_StaysWithinFileCountAndRedactsBeforeDisk()
    {
        using (var logger = new RollingFileLogger(_directory, "app", maxBytes: 180, maxFiles: 3))
        {
            for (var index = 0; index < 20; index++)
                logger.Info("test", $"message-{index} Authorization: Bearer secret-{index}-value");
            logger.Flush();
            Assert.Null(logger.LastError);
        }

        var files = Directory.GetFiles(_directory, "app.log*");
        Assert.InRange(files.Length, 1, 3);
        var content = string.Join("\n", files.Select(File.ReadAllText));
        Assert.DoesNotContain("secret-", content);
        Assert.Contains(SecretRedactor.Replacement, content);
    }

    [Fact]
    public void Export_FlushesAndRedactsEveryZipEntry()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "legacy.log"),
            "token=legacy-secret\nAuthorization: Bearer another-secret");
        var flushed = false;
        var zip = Path.Combine(Path.GetTempPath(), $"DesktopPet-diagnostics-{Guid.NewGuid():N}.zip");
        try
        {
            new DiagnosticExporter(_directory, () => flushed = true).Export(zip);

            Assert.True(flushed);
            using var archive = ZipFile.OpenRead(zip);
            Assert.Single(archive.Entries);
            using var reader = new StreamReader(archive.Entries[0].Open());
            var content = reader.ReadToEnd();
            Assert.DoesNotContain("legacy-secret", content);
            Assert.DoesNotContain("another-secret", content);
            Assert.Contains(SecretRedactor.Replacement, content);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public void AtomicFileWriter_PreservesOldTargetAndCleansTempOnPublicationFailure()
    {
        Directory.CreateDirectory(_directory);
        var target = Path.Combine(_directory, "sprite.png");
        File.WriteAllText(target, "old");
        using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Record.Exception(() => AtomicFileWriter.WriteAllBytes(target, [1, 2, 3]));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }

        Assert.Equal("old", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(_directory, ".sprite.png.*.tmp"));
    }


    [Fact]
    public void AtomicFileWriter_NonIoFailure_StillCleansTemp()
    {
        // write 委托抛非 IO 异常（如编码/业务异常）时：异常原样上抛，
        // 临时文件也必须清理（原实现清理代码在 try 内，非 IO 异常会跳过 → GUID tmp 累积）
        Directory.CreateDirectory(_directory);
        var target = Path.Combine(_directory, "sprite.png");

        var error = Record.Exception(() => AtomicFileWriter.Write(
            target,
            _ => throw new FormatException("boom")));

        Assert.IsType<FormatException>(error);
        Assert.Empty(Directory.GetFiles(_directory, ".sprite.png.*.tmp"));
    }

    [Fact]
    public void RollingLogger_TruncatesOversizedLineToHardByteLimit()
    {
        const int maxBytes = 180;
        using (var logger = new RollingFileLogger(_directory, "bounded", maxBytes, maxFiles: 2))
        {
            logger.Info("test", string.Join(' ', Enumerable.Repeat("verbose", 1_000)));
            logger.Flush();
        }

        var file = Assert.Single(Directory.GetFiles(_directory, "bounded.log*"));
        Assert.InRange(new FileInfo(file).Length, 1, maxBytes);
        Assert.Contains("[TRUNCATED]", File.ReadAllText(file));
    }

    [Fact]
    public void RollingLogger_RecoversAfterRotationSharingFailure()
    {
        var current = Path.Combine(_directory, "recover.log");
        Exception? lastError;
        using (var logger = new RollingFileLogger(_directory, "recover", maxBytes: 180, maxFiles: 2))
        {
            logger.Info("test", string.Join(' ', Enumerable.Repeat("entry", 24)));
            using (File.Open(current, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                logger.Info("test", "blocked rotation");
                Assert.NotNull(logger.LastError);
            }

            logger.Info("test", "after recovery");
            logger.Flush();
            lastError = logger.LastError;
        }

        Assert.Null(lastError);
        Assert.Contains(
            "after recovery",
            string.Join("\n", Directory.GetFiles(_directory, "recover.log*").Select(File.ReadAllText)));
    }

    [Fact]
    public void RollingLogger_CombineRotationFailure_AlwaysYieldsIOException()
    {
        // 轮转移动失败 + 重开失败双故障时：合并异常必须是 IOException 家族，
        // 因为 Write 的捕获过滤只接 IOException/UnauthorizedAccessException，
        // AggregateException 会逃逸中断调用方会话。
        var moveFailure = new IOException("move failed");
        var reopenFailure = new UnauthorizedAccessException("reopen failed");

        var combined = RollingFileLogger.CombineRotationFailure(moveFailure, reopenFailure);

        Assert.IsAssignableFrom<IOException>(combined);
        Assert.Same(reopenFailure, combined.InnerException);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
