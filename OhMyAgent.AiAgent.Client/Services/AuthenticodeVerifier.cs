using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using OhMyAgent.AiAgent.Client.Models;

namespace OhMyAgent.AiAgent.Client.Services;

/// <summary>
/// Authenticode 서명 검증(Windows 전용). WinVerifyTrust(wintrust.dll)로 신뢰 체인을 확인한다.
/// 모든 예외를 내부 흡수해 SignatureStatus로만 노출하며, 해시 검증 결과를 대체하지 않는 부가 정보다.
/// </summary>
public sealed class AuthenticodeVerifier : IAuthenticodeVerifier
{
    // WinVerifyTrust 액션 GUID: WINTRUST_ACTION_GENERIC_VERIFY_V2
    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone          = 2;
    private const uint WtdRevokeNone      = 0;
    private const uint WtdChoiceFile      = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose  = 2;
    private const uint WtdCacheOnlyUrlRetrieval = 0x00001000;

    // WinVerifyTrust 반환 코드(HRESULT).
    private const int ErrorSuccess               = 0;
    private const uint TrustENoSignature         = 0x800B0100;
    private const uint TrustEBadDigest           = 0x80096010;
    private const uint TrustEProviderUnknown     = 0x800B0001;
    private const uint TrustESubjectFormUnknown  = 0x800B0003;

    /// <inheritdoc />
    public SignatureStatus Verify(string filePath)
    {
        if (!OperatingSystem.IsWindows())
            return SignatureStatus.NotChecked;

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return SignatureStatus.NotChecked;

        try
        {
            return VerifyCore(filePath);
        }
        catch (Exception ex)
        {
            // 검증 자체 실패는 무효로 흡수(해시 결과를 대체하지 않음).
            AppLog.Warn("AuthenticodeVerifier", $"Verify failed for '{filePath}'", ex);
            return SignatureStatus.Invalid;
        }
    }

    private static SignatureStatus VerifyCore(string filePath)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct       = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath  = filePath,
            hFile          = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero
        };

        var pFileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFileInfo, false);

            var data = new WINTRUST_DATA
            {
                cbStruct            = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData      = IntPtr.Zero,
                dwUIChoice          = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice       = WtdChoiceFile,
                pFile               = pFileInfo,
                dwStateAction       = WtdStateActionVerify,
                hWVTStateData       = IntPtr.Zero,
                pwszURLReference    = null,
                dwProvFlags         = WtdCacheOnlyUrlRetrieval,
                dwUIContext         = 0
            };

            var action = WinTrustActionGenericVerifyV2;
            int result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            // 상태 핸들 정리.
            data.dwStateAction = WtdStateActionClose;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            return MapResult(result);
        }
        finally
        {
            Marshal.FreeHGlobal(pFileInfo);
        }
    }

    private static SignatureStatus MapResult(int result)
    {
        if (result == ErrorSuccess)
            return SignatureStatus.Valid;

        var hr = unchecked((uint)result);
        return hr switch
        {
            TrustENoSignature        => SignatureStatus.Unsigned,
            TrustESubjectFormUnknown => SignatureStatus.Unsigned,
            TrustEProviderUnknown    => SignatureStatus.Unsigned,
            TrustEBadDigest          => SignatureStatus.Invalid,
            _                        => SignatureStatus.Invalid
        };
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }
}
