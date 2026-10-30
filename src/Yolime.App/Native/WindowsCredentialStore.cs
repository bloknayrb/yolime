using System.Runtime.InteropServices;
using System.Text;
using Yolime.Core.Keys;

namespace Yolime.App.Native;

/// <summary>
/// Keys in Windows Credential Manager as generic credentials named <c>yolime/&lt;slot&gt;</c>. That is per-user storage
/// readable by anything running as this user, the same protection environment variables give, and no more.
/// </summary>
internal sealed unsafe partial class WindowsCredentialStore : ICredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public char* TargetName;
        public char* Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public byte* CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public char* TargetAlias;
        public char* UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint flags, out Credential* credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(Credential* credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(void* buffer);

    private static string Target(string name) => "yolime/" + name;

    public string? Read(string name)
    {
        if (!CredRead(Target(name), CredTypeGeneric, 0, out var cred)) return null;
        try
        {
            if (cred->CredentialBlob is null || cred->CredentialBlobSize == 0) return null;
            var span = new Span<byte>(cred->CredentialBlob, (int)cred->CredentialBlobSize);
            var key = Encoding.UTF8.GetString(span);
            span.Clear();
            return key;
        }
        finally
        {
            CredFree(cred);
        }
    }

    public bool Write(string name, string secret)
    {
        var blob = Encoding.UTF8.GetBytes(secret);
        try
        {
            fixed (char* target = Target(name))
            fixed (byte* data = blob)
            {
                // No UserName and no terminator: the blob is exactly the key's bytes.
                var cred = new Credential
                {
                    Type = CredTypeGeneric,
                    TargetName = target,
                    CredentialBlobSize = (uint)blob.Length,
                    CredentialBlob = data,
                    Persist = CredPersistLocalMachine,
                };
                return CredWrite(&cred, 0);
            }
        }
        finally
        {
            Array.Clear(blob);
        }
    }

    public bool Delete(string name) =>
        CredDelete(Target(name), CredTypeGeneric, 0) || Marshal.GetLastPInvokeError() == ErrorNotFound;
}
