using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace TechBench.Services;

public sealed class WindowsCredentialStore : ICredentialStore
{
    private const int ErrorNotFound = 1168;
    private const uint GenericCredentialType = 1;
    private const uint PersistLocalMachine = 2;
    private const int MaxCredentialBlobBytes = 2560;
    private const string TargetPrefix = "TechBench/";

    public string GetSecret(string key)
    {
        var targetName = BuildTargetName(key);
        if (!CredRead(targetName, GenericCredentialType, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return string.Empty;
            }

            throw new Win32Exception(error, $"Could not read the protected credential '{key}'.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }

            return Marshal.PtrToStringUni(
                    credential.CredentialBlob,
                    checked((int)credential.CredentialBlobSize / sizeof(char)))
                ?? string.Empty;
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void SetSecret(string key, string value)
    {
        var targetName = BuildTargetName(key);
        if (string.IsNullOrEmpty(value))
        {
            DeleteSecret(targetName, key);
            return;
        }

        var secretBytes = Encoding.Unicode.GetBytes(value);
        if (secretBytes.Length > MaxCredentialBlobBytes)
        {
            throw new ArgumentException("The credential is too long for Windows Credential Manager.", nameof(value));
        }

        var secretPointer = Marshal.AllocHGlobal(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, secretPointer, secretBytes.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredentialType,
                TargetName = targetName,
                CredentialBlobSize = checked((uint)secretBytes.Length),
                CredentialBlob = secretPointer,
                Persist = PersistLocalMachine,
                UserName = Environment.UserName
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not protect the credential '{key}'.");
            }
        }
        finally
        {
            Array.Clear(secretBytes, 0, secretBytes.Length);
            for (var index = 0; index < secretBytes.Length; index++)
            {
                Marshal.WriteByte(secretPointer, index, 0);
            }

            Marshal.FreeHGlobal(secretPointer);
        }
    }

    private static void DeleteSecret(string targetName, string key)
    {
        if (CredDelete(targetName, GenericCredentialType, 0))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
        {
            throw new Win32Exception(error, $"Could not delete the protected credential '{key}'.");
        }
    }

    private static string BuildTargetName(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Credential keys cannot be blank.", nameof(key));
        }

        return $"{TargetPrefix}{key.Trim()}";
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint reservedFlag,
        out IntPtr credentialPointer);

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr credentialPointer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public NativeFileTime LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }
}
