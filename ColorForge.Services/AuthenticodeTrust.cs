using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ColorForge.Services;
internal static class AuthenticodeTrust
{
    public static void Verify(string path, FileStream lockedFile)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Authenticode requires Windows.");
        Guid action = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        nint name = Marshal.StringToCoTaskMemUni(path);
        FileInfoData file = new() { Size = (uint)Marshal.SizeOf<FileInfoData>(), Path = name, Handle = lockedFile.SafeFileHandle.DangerousGetHandle() };
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfoData>());
        Marshal.StructureToPtr(file, pointer, false);
        TrustData data = new() { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, Revocation = 1, UnionChoice = 1,
            File = pointer, StateAction = 1, ProviderFlags = 0x80 };
        try {
            int status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (status != 0) throw new InvalidDataException($"Подпись установщика PawnIO не подтверждена Windows (0x{status:X8}).");
        }
        finally {
            data.StateAction = 2; WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.FreeHGlobal(pointer); Marshal.FreeCoTaskMem(name);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfoData { public uint Size; public nint Path, Handle, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData {
        public uint Size; public nint PolicyCallback, SipClient; public uint UiChoice, Revocation, UnionChoice;
        public nint File; public uint StateAction; public nint StateData, Url; public uint ProviderFlags, UiContext; public nint SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
}
