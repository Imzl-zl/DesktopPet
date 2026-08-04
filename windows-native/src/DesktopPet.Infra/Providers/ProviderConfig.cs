using System.Runtime.InteropServices;

namespace DesktopPet.Infra.Providers;

/// <summary>凭据存取抽象（apiKey 不落明文 JSON；测试用内存实现）。</summary>
public interface ICredentialStore
{
    string? Get(string key);
    void Set(string key, string value);
}

/// <summary>内存实现（测试用）。</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
    public void Set(string key, string value) => _values[key] = value;
}

/// <summary>Windows Credential Manager 实现（CredRead/CredWrite，target="DesktopPet/{key}"）。</summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    public string? Get(string key)
    {
        var target = "DesktopPet/" + key;
        if (!CredNative.CredRead(target, CredNative.CredTypeGeneric, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<CredNative.Credential>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredNative.CredFree(ptr);
        }
    }

    public void Set(string key, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var target = "DesktopPet/" + key;
        var cred = new CredNative.Credential
        {
            Type = CredNative.CredTypeGeneric,
            TargetName = target,
            CredentialBlob = Marshal.AllocCoTaskMem(bytes.Length),
            CredentialBlobSize = (uint)bytes.Length,
            Persist = CredNative.CredPersistLocalMachine,
            UserName = key,
        };
        try
        {
            Marshal.Copy(bytes, 0, cred.CredentialBlob, bytes.Length);
            CredNative.CredWrite(ref cred, 0);
        }
        finally
        {
            Marshal.FreeCoTaskMem(cred.CredentialBlob);
        }
    }
}

internal static class CredNative
{
    public const int CredTypeGeneric = 1;
    public const int CredPersistLocalMachine = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll")]
    public static extern void CredFree(IntPtr cred);
}
