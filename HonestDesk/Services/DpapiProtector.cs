using System.Runtime.InteropServices;
using System.Text;

namespace HonestDesk.Services;

public static class DpapiProtector
{
    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return "";
        }

        var bytes = Encoding.UTF8.GetBytes(plainText);
        return Convert.ToBase64String(Crypt(bytes, protect: true));
    }

    public static string Unprotect(string protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText))
        {
            return "";
        }

        try
        {
            var bytes = Convert.FromBase64String(protectedText);
            return Encoding.UTF8.GetString(Crypt(bytes, protect: false));
        }
        catch
        {
            return "";
        }
    }

    private static byte[] Crypt(byte[] data, bool protect)
    {
        var input = new DataBlob(data);
        var output = new DATA_BLOB();

        try
        {
            var ok = protect
                ? CryptProtectData(ref input.Blob, "HonestDesk", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output)
                : CryptUnprotectData(ref input.Blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output);

            if (!ok)
            {
                throw new InvalidOperationException($"DPAPI error {Marshal.GetLastWin32Error()}");
            }

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, result.Length);
            return result;
        }
        finally
        {
            input.Dispose();
            if (output.pbData != IntPtr.Zero)
            {
                LocalFree(output.pbData);
            }
        }
    }

    private sealed class DataBlob : IDisposable
    {
        public DATA_BLOB Blob;

        public DataBlob(byte[] data)
        {
            Blob.cbData = data.Length;
            Blob.pbData = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, Blob.pbData, data.Length);
        }

        public void Dispose()
        {
            if (Blob.pbData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Blob.pbData);
                Blob.pbData = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
