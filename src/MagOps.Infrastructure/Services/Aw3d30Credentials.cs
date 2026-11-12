using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace MagOps.Infrastructure.Services;

public sealed record Aw3d30Login(string UserName, string Password)
{
    public override string ToString() => "AW3D30 本机凭据（已隐藏）";
}

/// <summary>Windows current-user DPAPI; never shipped with the application.</summary>
public static class Aw3d30Credentials
{
    internal static string? StoragePathOverride { get; set; }
    public static string FilePath => StoragePathOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagOps", "aw3d30-credentials.bin");
    public static Aw3d30Login? Load()
    {
        if (!File.Exists(FilePath)) return null;
        byte[] plain = Transform(File.ReadAllBytes(FilePath), false);
        try { return JsonSerializer.Deserialize<Aw3d30Login>(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static void Save(Aw3d30Login login)
    {
        if (string.IsNullOrWhiteSpace(login.UserName) || string.IsNullOrWhiteSpace(login.Password) || login.UserName.Contains(':'))
            throw new ArgumentException("请填写有效的 AW3D30 账号和密码。");
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(login);
        try { var bytes = Transform(plain, true); AtomicFileWriter.Write(FilePath, s => s.Write(bytes)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    private static byte[] Transform(byte[] bytes, bool encrypt)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("默认账号使用 Windows 当前用户加密存储。");
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = encrypt ? CryptProtectData(ref input, "MagOps AW3D30", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("无法读写当前 Windows 用户的 AW3D30 凭据，请重新设置账号。");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            Marshal.Copy(new byte[input.Length], 0, input.Data, input.Length); Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) { Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length); LocalFree(output.Data); }
        }
    }
}
