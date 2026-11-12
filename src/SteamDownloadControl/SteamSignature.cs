using System.Runtime.InteropServices;
using System.Text;

namespace SteamDownloadControl;

internal static class SteamSignature
{
    private const uint Verify = 1;
    private const uint Close = 2;
    private const uint NoUi = 2;
    private const uint ChoiceFile = 1;
    private const uint CacheOnlyUrlRetrieval = 0x1000;
    private const uint SimpleDisplayName = 4;
    private static readonly Guid AuthenticodePolicy = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static bool IsValid(FileStream dll)
    {
        var filePath = Marshal.StringToHGlobalUni(dll.Name);
        var fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(),
            UiChoice = NoUi,
            UnionChoice = ChoiceFile,
            FileInfo = fileInfo,
            StateAction = Verify,
            ProviderFlags = CacheOnlyUrlRetrieval
        };
        try
        {
            Marshal.StructureToPtr(new TrustFile
            {
                Size = (uint)Marshal.SizeOf<TrustFile>(),
                Path = filePath,
                Handle = dll.SafeFileHandle.DangerousGetHandle()
            }, fileInfo, fDeleteOld: false);
            if (WinVerifyTrust(-1, in AuthenticodePolicy, ref data) != 0 || data.StateData == 0)
                return false;

            var provider = WTHelperProvDataFromStateData(data.StateData);
            var signer = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            var leaf = signer == 0 ? 0 : WTHelperGetProvCertFromChain(signer, 0);
            if (leaf == 0)
                return false;
            var certificate = Marshal.PtrToStructure<ProviderCertificate>(leaf).Certificate;
            if (certificate == 0)
                return false;
            var publisher = new StringBuilder(64);
            return CertGetNameStringW(certificate, SimpleDisplayName, 0, 0, publisher, publisher.Capacity) != 0 &&
                string.Equals(publisher.ToString(), "Valve Corp.", StringComparison.Ordinal);
        }
        finally
        {
            if (data.StateData != 0)
            {
                data.StateAction = Close;
                WinVerifyTrust(-1, in AuthenticodePolicy, ref data);
            }
            Marshal.FreeHGlobal(fileInfo);
            Marshal.FreeHGlobal(filePath);
            dll.Position = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustFile
    {
        public uint Size;
        public nint Path;
        public nint Handle;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint FileInfo;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    // Only the first two members of CRYPT_PROVIDER_CERT are needed.
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificate
    {
        public uint Size;
        public nint Certificate;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(nint window, in Guid action, ref TrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperProvDataFromStateData(nint stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvSignerFromChain(nint provider, uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvCertFromChain(nint signer, uint certificateIndex);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CertGetNameStringW(nint certificate, uint type, uint flags, nint parameter,
        StringBuilder name, int capacity);
}
