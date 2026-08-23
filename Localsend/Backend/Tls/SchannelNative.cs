using System;
using System.Runtime.InteropServices;

namespace Localsend.Backend.Tls
{
    /// <summary>
    /// The Compact Framework does not ship the Schannel managed wrappers.
    /// Keep the native declarations in one file so the desktop provider can
    /// be loaded lazily and the CE process never resolves secur32.dll.
    /// </summary>
    internal static class SchannelNative
    {
        internal const string SecurityPackage = "Microsoft Unified Security Protocol Provider";
        internal const uint SecurityNativeDrep = 0x00000010;

        internal const uint SecPkgCredInbound = 0x00000001;
        internal const uint SecPkgCredOutbound = 0x00000002;
        internal const uint SecPkgCredBoth = 0x00000003;

        internal const uint SchannelCredVersion = 0x00000004;
        internal const uint SchCredNoSystemMapper = 0x00000002;
        internal const uint SchCredNoServerNameCheck = 0x00000004;
        internal const uint SchCredManualCredValidation = 0x00000008;
        internal const uint SchCredNoDefaultCreds = 0x00000010;

        // TLS 1.2 was added after the WM6 SDK header was published.  These
        // values are the stable Schannel protocol bit assignments.
        internal const uint SpProtTls12Server = 0x00000400;
        internal const uint SpProtTls12Client = 0x00000800;

        internal const uint IscReqMutualAuth = 0x00000002;
        internal const uint IscReqReplayDetect = 0x00000004;
        internal const uint IscReqSequenceDetect = 0x00000008;
        internal const uint IscReqConfidentiality = 0x00000010;
        internal const uint IscReqStream = 0x00008000;
        internal const uint IscReqManualCredValidation = 0x00080000;

        internal const uint AscReqMutualAuth = 0x00000002;
        internal const uint AscReqReplayDetect = 0x00000004;
        internal const uint AscReqSequenceDetect = 0x00000008;
        internal const uint AscReqConfidentiality = 0x00000010;
        internal const uint AscReqStream = 0x00010000;

        internal const uint SecBufferVersion = 0;
        internal const uint SecBufferEmpty = 0;
        internal const uint SecBufferData = 1;
        internal const uint SecBufferToken = 2;
        internal const uint SecBufferMissing = 4;
        internal const uint SecBufferExtra = 5;
        internal const uint SecBufferStreamTrailer = 6;
        internal const uint SecBufferStreamHeader = 7;
        internal const uint SecBufferStream = 10;

        internal const uint SecPkgAttrStreamSizes = 4;
        internal const uint SecPkgAttrRemoteCertContext = 0x53;
        internal const uint SecPkgAttrConnectionInfo = 0x5a;

        internal const int SecESuccess = 0x00000000;
        internal const int SecIContinueNeeded = 0x00090312;
        internal const int SecICompleteNeeded = 0x00090313;
        internal const int SecICompleteAndContinue = 0x00090314;
        internal const int SecIContextExpired = 0x00090317;
        internal const int SecIIncompleteCredentials = 0x00090320;
        internal const int SecIRenegotiate = 0x00090321;
        internal const int SecEIncompleteMessage = unchecked((int)0x80090318);
        internal const int SecEContextExpired = unchecked((int)0x80100006);

        internal const uint X509AsnEncoding = 0x00000001;
        internal const uint CertX500NameStr = 3;
        internal const uint ProvRsaFull = 1;
        internal const uint AtKeyExchange = 1;
        internal const uint CalgRsaKeyx = 0x0000a400;
        internal const uint CalgSha256 = 0x0000800c;
        internal const uint CryptNewKeyset = 0x00000008;
        internal const uint CryptMachineKeyset = 0x00000020;
        internal const uint CryptSilent = 0x00000040;
        internal const uint CryptExportable = 0x00000001;
        internal const uint CertKeyProvInfoPropId = 2;
        internal const uint CertKeyContextPropId = 5;
        internal const uint CertSetKeyContextPropId = 1;
        internal const uint CertCreateSelfSignNoKeyInfo = 2;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SecHandle
        {
            public IntPtr dwLower;
            public IntPtr dwUpper;

            public bool IsZero
            {
                get { return dwLower == IntPtr.Zero && dwUpper == IntPtr.Zero; }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SecBuffer
        {
            public uint cbBuffer;
            public uint BufferType;
            public IntPtr pvBuffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SecBufferDesc
        {
            public uint ulVersion;
            public uint cBuffers;
            public IntPtr pBuffers;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SchannelCred
        {
            public uint dwVersion;
            public uint cCreds;
            public IntPtr paCred;
            public IntPtr hRootStore;
            public uint cMappers;
            public IntPtr aphMappers;
            public uint cSupportedAlgs;
            public IntPtr palgSupportedAlgs;
            public uint grbitEnabledProtocols;
            public uint dwMinimumCipherStrength;
            public uint dwMaximumCipherStrength;
            public uint dwSessionLifespan;
            public uint dwFlags;
            public uint dwCredFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct StreamSizes
        {
            public uint cbHeader;
            public uint cbTrailer;
            public uint cbMaximumMessage;
            public uint cBuffers;
            public uint cbBlockSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ConnectionInfo
        {
            public uint dwProtocol;
            public uint aiCipher;
            public uint dwCipherStrength;
            public uint aiHash;
            public uint dwHashStrength;
            public uint aiExch;
            public uint dwExchStrength;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct CertContext
        {
            public uint dwCertEncodingType;
            public IntPtr pbCertEncoded;
            public uint cbCertEncoded;
            public IntPtr pCertInfo;
            public IntPtr hCertStore;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Blob
        {
            public uint cbData;
            public IntPtr pbData;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct AlgorithmIdentifier
        {
            public IntPtr pszObjId;
            public Blob Parameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NameBlob
        {
            public uint cbData;
            public IntPtr pbData;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SystemTime
        {
            public ushort wYear;
            public ushort wMonth;
            public ushort wDayOfWeek;
            public ushort wDay;
            public ushort wHour;
            public ushort wMinute;
            public ushort wSecond;
            public ushort wMilliseconds;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct CryptKeyProvInfo
        {
            public IntPtr pwszContainerName;
            public IntPtr pwszProvName;
            public uint dwProvType;
            public uint dwFlags;
            public uint cProvParam;
            public IntPtr rgProvParam;
            public uint dwKeySpec;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct CertKeyContext
        {
            public uint cbSize;
            public IntPtr hCryptProv;
            public uint dwKeySpec;
        }

        [DllImport("secur32.dll", EntryPoint = "AcquireCredentialsHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int AcquireCredentialsHandle(
            string pszPrincipal,
            string pszPackage,
            uint fCredentialUse,
            IntPtr pvLogonId,
            IntPtr pAuthData,
            IntPtr pGetKeyFn,
            IntPtr pvGetKeyArgument,
            ref SecHandle phCredential,
            IntPtr ptsExpiry);

        [DllImport("secur32.dll", EntryPoint = "FreeCredentialsHandle", SetLastError = true)]
        internal static extern int FreeCredentialsHandle(ref SecHandle phCredential);

        [DllImport("secur32.dll", EntryPoint = "InitializeSecurityContextW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int InitializeSecurityContext(
            ref SecHandle phCredential,
            IntPtr phContext,
            string pszTargetName,
            uint fContextReq,
            uint Reserved1,
            uint TargetDataRep,
            IntPtr pInput,
            uint Reserved2,
            ref SecHandle phNewContext,
            IntPtr pOutput,
            out uint pfContextAttr,
            IntPtr ptsExpiry);

        [DllImport("secur32.dll", EntryPoint = "AcceptSecurityContext", SetLastError = true)]
        internal static extern int AcceptSecurityContext(
            ref SecHandle phCredential,
            IntPtr phContext,
            IntPtr pInput,
            uint fContextReq,
            uint TargetDataRep,
            ref SecHandle phNewContext,
            IntPtr pOutput,
            out uint pfContextAttr,
            IntPtr ptsExpiry);

        [DllImport("secur32.dll", EntryPoint = "CompleteAuthToken", SetLastError = true)]
        internal static extern int CompleteAuthToken(ref SecHandle phContext, IntPtr pToken);

        [DllImport("secur32.dll", EntryPoint = "DeleteSecurityContext", SetLastError = true)]
        internal static extern int DeleteSecurityContext(ref SecHandle phContext);

        [DllImport("secur32.dll", EntryPoint = "QueryContextAttributesW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int QueryContextAttributes(ref SecHandle phContext, uint ulAttribute, IntPtr pBuffer);

        [DllImport("secur32.dll", EntryPoint = "FreeContextBuffer", SetLastError = true)]
        internal static extern int FreeContextBuffer(IntPtr pvContextBuffer);

        [DllImport("secur32.dll", EntryPoint = "EncryptMessage", SetLastError = true)]
        internal static extern int EncryptMessage(ref SecHandle phContext, uint fQOP, IntPtr pMessage, uint MessageSeqNo);

        [DllImport("secur32.dll", EntryPoint = "DecryptMessage", SetLastError = true)]
        internal static extern int DecryptMessage(ref SecHandle phContext, IntPtr pMessage, uint MessageSeqNo, IntPtr pfQOP);

        [DllImport("advapi32.dll", EntryPoint = "CryptAcquireContextW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool CryptAcquireContext(
            out IntPtr phProv,
            string szContainer,
            string szProvider,
            uint dwProvType,
            uint dwFlags);

        [DllImport("advapi32.dll", EntryPoint = "CryptReleaseContext", SetLastError = true)]
        internal static extern bool CryptReleaseContext(IntPtr hProv, uint dwFlags);

        [DllImport("advapi32.dll", EntryPoint = "CryptGenKey", SetLastError = true)]
        internal static extern bool CryptGenKey(IntPtr hProv, uint Algid, uint dwFlags, out IntPtr phKey);

        [DllImport("advapi32.dll", EntryPoint = "CryptGetUserKey", SetLastError = true)]
        internal static extern bool CryptGetUserKey(IntPtr hProv, uint dwKeySpec, out IntPtr phKey);

        [DllImport("advapi32.dll", EntryPoint = "CryptDestroyKey", SetLastError = true)]
        internal static extern bool CryptDestroyKey(IntPtr hKey);

        [DllImport("crypt32.dll", EntryPoint = "CertStrToNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool CertStrToName(
            uint dwCertEncodingType,
            string pszX500,
            uint dwStrType,
            IntPtr pvReserved,
            byte[] pbEncoded,
            ref uint pcbEncoded,
            IntPtr ppszError);

        [DllImport("crypt32.dll", EntryPoint = "CertCreateSelfSignCertificate", SetLastError = true)]
        internal static extern IntPtr CertCreateSelfSignCertificate(
            IntPtr hProv,
            ref NameBlob pSubjectIssuerBlob,
            uint dwFlags,
            IntPtr pKeyProvInfo,
            IntPtr pSignatureAlgorithm,
            IntPtr pStartTime,
            IntPtr pEndTime,
            IntPtr pExtensions);

        [DllImport("crypt32.dll", EntryPoint = "CertCreateCertificateContext", SetLastError = true)]
        internal static extern IntPtr CertCreateCertificateContext(
            uint dwCertEncodingType,
            byte[] pbCertEncoded,
            uint cbCertEncoded);

        [DllImport("crypt32.dll", EntryPoint = "CertFreeCertificateContext", SetLastError = true)]
        internal static extern bool CertFreeCertificateContext(IntPtr pCertContext);

        [DllImport("crypt32.dll", EntryPoint = "CertSetCertificateContextProperty", SetLastError = true)]
        internal static extern bool CertSetCertificateContextProperty(
            IntPtr pCertContext,
            uint dwPropId,
            uint dwFlags,
            IntPtr pvData);

        [DllImport("crypt32.dll", EntryPoint = "CryptAcquireCertificatePrivateKey", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CryptAcquireCertificatePrivateKey(
            IntPtr pCert,
            uint dwFlags,
            IntPtr pvParameters,
            out IntPtr phCryptProvOrNCryptKey,
            out uint pdwKeySpec,
            [MarshalAs(UnmanagedType.Bool)] out bool pfCallerFreeProvOrNCryptKey);
    }
}
