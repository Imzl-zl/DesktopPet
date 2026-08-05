using System.Text;
using DesktopPet.Infra.Providers;

namespace DesktopPet.Infra.Tests;

/// <summary>
/// WindowsCredentialStore.DecodeBlob：cmdkey 写入的凭据 blob 是 UTF-16LE，
/// 本应用 Set 写入的是 UTF-8——解码必须兼容两者，否则 apiKey 含 NUL 乱码
/// 导致 Authorization 头被网关 400 拒绝（真实验收发现的根因）。
/// </summary>
public class CredentialDecodeTests
{
    [Fact]
    public void DecodeBlob_Utf16LeFromCmdkey()
    {
        var bytes = Encoding.Unicode.GetBytes("sk-1FEwyeO37qqseJqIYo3nCnP1dXSYpFuI54xMN3TDuW80exAt");
        Assert.Equal("sk-1FEwyeO37qqseJqIYo3nCnP1dXSYpFuI54xMN3TDuW80exAt",
            WindowsCredentialStore.DecodeBlob(bytes));
    }

    [Fact]
    public void DecodeBlob_Utf8FromAppSet()
    {
        var bytes = Encoding.UTF8.GetBytes("sk-abc-中文key");
        Assert.Equal("sk-abc-中文key", WindowsCredentialStore.DecodeBlob(bytes));
    }

    [Fact]
    public void DecodeBlob_EmptyBlob_ReturnsEmpty()
    {
        Assert.Equal("", WindowsCredentialStore.DecodeBlob([]));
    }

    [Fact]
    public void DecodeBlob_AsciiBytes_Utf8Fallback()
    {
        // 纯 ASCII 且无 \0 间隔：必须走 UTF-8 而非被误判 UTF-16
        Assert.Equal("plain-ascii", WindowsCredentialStore.DecodeBlob(Encoding.UTF8.GetBytes("plain-ascii")));
    }
}
