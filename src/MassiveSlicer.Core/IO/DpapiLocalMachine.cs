using System.Runtime.InteropServices;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Decrypt a blob protected with CryptProtectData + CRYPTPROTECT_LOCAL_MACHINE.
/// Any login on this PC can read it. No extra NuGet — shop SMB restores are fragile.
/// </summary>
static class DpapiLocalMachine
{
    const uint CryptProtectLocalMachine = 0x4;

    public static byte[] Unprotect(byte[] cipher)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI is Windows-only.");
        if (cipher.Length == 0)
            throw new ArgumentException("empty blob", nameof(cipher));

        var input = new DataBlob { cbData = cipher.Length, pbData = Marshal.AllocHGlobal(cipher.Length) };
        try
        {
            Marshal.Copy(cipher, 0, input.pbData, cipher.Length);
            if (!CryptUnprotectData(
                    ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectLocalMachine, out var plain))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                var result = new byte[plain.cbData];
                Marshal.Copy(plain.pbData, result, 0, plain.cbData);
                return result;
            }
            finally
            {
                if (plain.pbData != IntPtr.Zero)
                    LocalFree(plain.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr hMem);
}
