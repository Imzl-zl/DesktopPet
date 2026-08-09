using System.Runtime.InteropServices;

namespace DesktopPet.Infra.Providers;

/// <summary>凭据存取抽象（apiKey 不落明文 JSON；测试用内存实现）。</summary>
public interface ICredentialStore
{
    string? Get(string key);
    void Set(string key, string value);
    bool Delete(string key);
}

public interface ICredentialNamespaceCleaner
{
    int DeleteAll();
}

public sealed class CredentialStoreException : IOException
{
    public CredentialStoreException(string operation, int nativeError)
        : base($"Windows 凭据{operation}失败（系统错误 {nativeError}）")
    {
        Operation = operation;
        NativeError = nativeError;
    }

    public string Operation { get; }
    public int NativeError { get; }
}

/// <summary>内存实现（测试用）。</summary>
public sealed class InMemoryCredentialStore : ICredentialStore, ICredentialNamespaceCleaner
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
    public void Set(string key, string value) => _values[key] = value;
    public bool Delete(string key) => _values.Remove(key);
    public int DeleteAll()
    {
        var count = _values.Count;
        _values.Clear();
        return count;
    }
}

/// <summary>Windows Credential Manager 实现（CredRead/CredWrite，target="DesktopPet/{key}"）。</summary>
public sealed class WindowsCredentialStore : ICredentialStore, ICredentialNamespaceCleaner
{
    public string? Get(string key)
    {
        var target = "DesktopPet/" + key;
        if (!CredNative.CredRead(target, CredNative.CredTypeGeneric, 0, out var ptr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == CredNative.ErrorNotFound) return null;
            throw new CredentialStoreException("读取", error);
        }
        try
        {
            var cred = Marshal.PtrToStructure<CredNative.Credential>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return DecodeBlob(bytes);
        }
        finally
        {
            CredNative.CredFree(ptr);
        }
    }

    /// <summary>
    /// 凭据 blob 解码：cmdkey 等工具写入的是 UTF-16LE（含 \0 间隔），
    /// 本应用 Set 写入的是 UTF-8。检测 \0 特征后选择解码，避免 apiKey
    /// 变成含 NUL 的乱码（曾导致 Authorization 头被网关 400 拒绝）。
    /// </summary>
    public static string DecodeBlob(byte[] bytes)
    {
        var isUtf16 = bytes.Length >= 2
            && bytes[^1] == 0
            && Enumerable.Range(0, bytes.Length / 2).All(i => bytes[i * 2 + 1] == 0);
        return isUtf16
            ? System.Text.Encoding.Unicode.GetString(bytes)
            : System.Text.Encoding.UTF8.GetString(bytes);
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
            if (!CredNative.CredWrite(ref cred, 0))
                throw new CredentialStoreException("写入", Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeCoTaskMem(cred.CredentialBlob);
        }
    }

    public bool Delete(string key)
    {
        if (CredNative.CredDelete("DesktopPet/" + key, CredNative.CredTypeGeneric, 0)) return true;
        var error = Marshal.GetLastWin32Error();
        if (error == CredNative.ErrorNotFound) return false;
        throw new CredentialStoreException("删除", error);
    }

    public int DeleteAll()
    {
        if (!CredNative.CredEnumerate("DesktopPet/*", 0, out var count, out var credentials))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == CredNative.ErrorNotFound) return 0;
            throw new CredentialStoreException("枚举", error);
        }

        var targets = new List<string>(count);
        try
        {
            for (var index = 0; index < count; index++)
            {
                var credentialPointer = Marshal.ReadIntPtr(credentials, index * IntPtr.Size);
                var credential = Marshal.PtrToStructure<CredNative.Credential>(credentialPointer);
                if (!string.IsNullOrEmpty(credential.TargetName)) targets.Add(credential.TargetName);
            }
        }
        finally
        {
            CredNative.CredFree(credentials);
        }

        var deleted = 0;
        foreach (var target in targets)
        {
            if (CredNative.CredDelete(target, CredNative.CredTypeGeneric, 0))
            {
                deleted++;
                continue;
            }
            var error = Marshal.GetLastWin32Error();
            if (error != CredNative.ErrorNotFound)
                throw new CredentialStoreException("删除", error);
        }
        return deleted;
    }
}

internal static class CredNative
{
    public const int CredTypeGeneric = 1;
    public const int CredPersistLocalMachine = 2;
    public const int ErrorNotFound = 1168;

    // 注意：必须 CharSet.Unicode——CREDENTIALW 的字符串字段是 LPWSTR，
    // 缺省 CharSet 会按 ANSI 编组，CredWrite 写出的 target 名会被系统按
    // UTF-16 误解成乱码（曾导致迁移器每次启动误报 target-conflict 并累积垃圾凭据）。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CredEnumerate(
        string filter,
        int flags,
        out int count,
        out IntPtr credentials);

    [DllImport("advapi32.dll")]
    public static extern void CredFree(IntPtr cred);
}
