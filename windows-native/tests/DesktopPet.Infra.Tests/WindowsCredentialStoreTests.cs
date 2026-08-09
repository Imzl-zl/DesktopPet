using DesktopPet.Infra.Providers;

namespace DesktopPet.Infra.Tests;

public sealed class WindowsCredentialStoreTests
{
    [Fact]
    public void SetGetDelete_RoundTrip_PreservesTargetNameExactly()
    {
        if (!OperatingSystem.IsWindows()) return;

        // 回归防护：CredNative.Credential 曾缺 CharSet.Unicode，CredWrite 把
        // target 名按 ANSI 编组、系统按 UTF-16 解释成乱码名，导致随后
        // CredRead 永远 NOT_FOUND（迁移器误报 target-conflict 并累积垃圾凭据）。
        var store = new WindowsCredentialStore();
        var key = "provider/test/" + Guid.NewGuid().ToString("N") + "/api-key";
        const string secret = "sk-test-roundtrip";
        try
        {
            store.Set(key, secret);

            var readBack = store.Get(key);

            Assert.Equal(secret, readBack);
        }
        finally
        {
            store.Delete(key);
        }
    }
}
