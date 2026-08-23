using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Localsend.Backend.Runtime;
using Localsend.Backend.Util;

namespace Localsend.Backend.Tls
{
    /// <summary>
    /// A persisted RSA-2048 identity backed by the Windows legacy CSP.  The
    /// certificate bytes are kept in the app data directory while the
    /// private key stays in the user's CryptoAPI key container.
    /// </summary>
    internal sealed class SchannelIdentity : IDisposable
    {
        private const string ContainerName = "LocalSend-WinForms-TLS";
        private const string SubjectName = "CN=LocalSend device";
        private const string SignatureOid = "1.2.840.113549.1.1.11"; // sha256WithRSAEncryption

        private bool _disposed;

        public IntPtr ProviderHandle { get; private set; }
        public IntPtr CertificateContext { get; private set; }
        public byte[] CertificateDer { get; private set; }
        public string Fingerprint { get; private set; }
        public string CertificatePath { get; private set; }

        private SchannelIdentity() { }

        public static SchannelIdentity LoadOrCreate(RuntimeEnvironmentInfo environment)
        {
            string dir = SelectWritableDirectory(GetIdentityDirectory(environment), environment);
            Directory.CreateDirectory(dir);
            string certPath = Path.Combine(dir, "identity.der");

            string providerName;
            uint providerType;
            uint providerFlags;
            IntPtr hProv = AcquireProvider(false, out providerName, out providerType, out providerFlags);
            if (hProv == IntPtr.Zero)
            {
                hProv = AcquireProvider(true, out providerName, out providerType, out providerFlags);
            }
            if (hProv == IntPtr.Zero)
                throw new InvalidOperationException("CryptoAPI key container unavailable (" + LastError() + ")");

            try
            {
                byte[] der = null;
                IntPtr cert = IntPtr.Zero;
                if (File.Exists(certPath))
                {
                    using (FileStream fs = new FileStream(certPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (fs.Length > 1024 * 1024) throw new InvalidDataException("TLS identity certificate is too large");
                        der = new byte[(int)fs.Length];
                        int read = 0;
                        while (read < der.Length)
                        {
                            int n = fs.Read(der, read, der.Length - read);
                            if (n <= 0) throw new EndOfStreamException("TLS identity certificate is truncated");
                            read += n;
                        }
                    }
                    cert = SchannelNative.CertCreateCertificateContext(
                        SchannelNative.X509AsnEncoding, der, (uint)der.Length);
                    if (cert == IntPtr.Zero)
                        throw new InvalidDataException("TLS identity certificate is not a valid DER certificate");

                    if (!SetKeyProviderInfo(cert, hProv, providerName, providerType, providerFlags))
                    {
                        SchannelNative.CertFreeCertificateContext(cert);
                        throw new InvalidOperationException("TLS identity private key is unavailable (" + LastError() + ")");
                    }
                }
                else
                {
                    cert = CreateSelfSigned(hProv, providerName, providerType, providerFlags, out der);
                    if (cert == IntPtr.Zero)
                        throw new InvalidOperationException("Unable to create TLS identity (" + LastError() + ")");
                    WriteCertificateAtomically(certPath, der);
                }

                SchannelIdentity identity = new SchannelIdentity();
                ValidatePrivateKey(cert);
                identity.ProviderHandle = hProv;
                identity.CertificateContext = cert;
                identity.CertificateDer = der;
                identity.Fingerprint = Sha256.ToUpperHex(Sha256.Compute(der));
                identity.CertificatePath = certPath;
                return identity;
            }
            catch
            {
                try { SchannelNative.CryptReleaseContext(hProv, 0); } catch { }
                throw;
            }
        }

        private static string GetIdentityDirectory(RuntimeEnvironmentInfo environment)
        {
            if (environment != null && environment.IsWindowsCe)
                return @"\Application Data\LocalSend";

            string appData = "";
            try { appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); }
            catch { }
            if (string.IsNullOrEmpty(appData)) appData = Directory.GetCurrentDirectory();
            return Path.Combine(appData, "LocalSend-WinForms");
        }

        private static string SelectWritableDirectory(string preferred, RuntimeEnvironmentInfo environment)
        {
            try
            {
                Directory.CreateDirectory(preferred);
                string probe = Path.Combine(preferred, ".identity-write-test");
                using (FileStream fs = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None))
                    fs.WriteByte(0);
                File.Delete(probe);
                return preferred;
            }
            catch
            {
                string fallback;
                if (environment != null && environment.IsWindowsCe)
                    fallback = @"\My Documents\LocalSend";
                else
                    fallback = Path.Combine(Directory.GetCurrentDirectory(), "LocalSendData");
                try { Directory.CreateDirectory(fallback); } catch { }
                return fallback;
            }
        }

        private static IntPtr AcquireProvider(bool create, out string selectedName, out uint selectedType, out uint selectedFlags)
        {
            string[] names = new string[]
            {
                "Microsoft Enhanced RSA and AES Cryptographic Provider",
                "Microsoft Enhanced Cryptographic Provider v1.0",
                "Microsoft Base Cryptographic Provider v1.0",
                null
            };
            uint[] types = new uint[] { 24U, SchannelNative.ProvRsaFull, SchannelNative.ProvRsaFull, 24U };
            uint baseFlags = SchannelNative.CryptSilent | (create ? SchannelNative.CryptNewKeyset : 0U);
            uint[] flagVariants = new uint[] { baseFlags, baseFlags | SchannelNative.CryptMachineKeyset };
            for (int f = 0; f < flagVariants.Length; f++)
            {
                for (int i = 0; i < names.Length; i++)
                {
                    IntPtr hProv;
                    if (SchannelNative.CryptAcquireContext(
                        out hProv, ContainerName, names[i], types[i], flagVariants[f]))
                    {
                        selectedName = names[i];
                        selectedType = types[i];
                        selectedFlags = flagVariants[f];
                        return hProv;
                    }
                }
            }
            selectedName = null;
            selectedType = 0;
            selectedFlags = 0;
            return IntPtr.Zero;
        }

        private static IntPtr CreateSelfSigned(IntPtr hProv, string providerName, uint providerType, uint providerFlags, out byte[] der)
        {
            der = null;
            IntPtr hKey = IntPtr.Zero;
            bool haveKey = SchannelNative.CryptGetUserKey(
                hProv, SchannelNative.AtKeyExchange, out hKey);
            if (!haveKey)
            {
                uint keyFlags = (2048U << 16) | SchannelNative.CryptExportable;
                if (!SchannelNative.CryptGenKey(hProv, SchannelNative.CalgRsaKeyx, keyFlags, out hKey))
                    return IntPtr.Zero;
            }

            IntPtr nameBytes = IntPtr.Zero;
            IntPtr oid = IntPtr.Zero;
            IntPtr keyInfo = IntPtr.Zero;
            IntPtr algorithm = IntPtr.Zero;
            IntPtr start = IntPtr.Zero;
            IntPtr end = IntPtr.Zero;
            IntPtr container = IntPtr.Zero;
            IntPtr provider = IntPtr.Zero;
            IntPtr cert = IntPtr.Zero;
            try
            {
                uint nameLength = 0;
                if (!SchannelNative.CertStrToName(
                    SchannelNative.X509AsnEncoding, SubjectName,
                    SchannelNative.CertX500NameStr, IntPtr.Zero,
                    null, ref nameLength, IntPtr.Zero))
                    return IntPtr.Zero;

                byte[] name = new byte[(int)nameLength];
                if (!SchannelNative.CertStrToName(
                    SchannelNative.X509AsnEncoding, SubjectName,
                    SchannelNative.CertX500NameStr, IntPtr.Zero,
                    name, ref nameLength, IntPtr.Zero))
                    return IntPtr.Zero;
                nameBytes = Marshal.AllocHGlobal((int)nameLength);
                Marshal.Copy(name, 0, nameBytes, (int)nameLength);
                SchannelNative.NameBlob nameBlob = new SchannelNative.NameBlob();
                nameBlob.cbData = nameLength;
                nameBlob.pbData = nameBytes;

                oid = AllocString(SignatureOid, false);
                SchannelNative.AlgorithmIdentifier alg = new SchannelNative.AlgorithmIdentifier();
                alg.pszObjId = oid;
                alg.Parameters.cbData = 0;
                alg.Parameters.pbData = IntPtr.Zero;
                algorithm = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SchannelNative.AlgorithmIdentifier)));
                Marshal.StructureToPtr(alg, algorithm, false);

                SchannelNative.CryptKeyProvInfo info = BuildKeyProviderInfo(providerName, providerType, providerFlags);
                container = AllocString(ContainerName, true);
                provider = string.IsNullOrEmpty(providerName) ? IntPtr.Zero : AllocString(providerName, true);
                info.pwszContainerName = container;
                info.pwszProvName = provider;
                keyInfo = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SchannelNative.CryptKeyProvInfo)));
                Marshal.StructureToPtr(info, keyInfo, false);

                DateTime now = DateTime.UtcNow;
                SchannelNative.SystemTime from = ToSystemTime(now.AddDays(-1));
                SchannelNative.SystemTime until = ToSystemTime(now.AddDays(365));
                start = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SchannelNative.SystemTime)));
                end = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SchannelNative.SystemTime)));
                Marshal.StructureToPtr(from, start, false);
                Marshal.StructureToPtr(until, end, false);

                cert = SchannelNative.CertCreateSelfSignCertificate(
                    hProv, ref nameBlob, 0, keyInfo, algorithm,
                    start, end, IntPtr.Zero);
                if (cert == IntPtr.Zero) return IntPtr.Zero;
                // Some modern CSPs do not copy the provider property when
                // CertCreateSelfSignCertificate is given a legacy provider
                // handle.  Set it explicitly so Schannel can locate the key.
                if (!SetKeyProviderInfo(cert, hProv, providerName, providerType, providerFlags))
                {
                    SchannelNative.CertFreeCertificateContext(cert);
                    cert = IntPtr.Zero;
                    return IntPtr.Zero;
                }

                SchannelNative.CertContext context = (SchannelNative.CertContext)
                    Marshal.PtrToStructure(cert, typeof(SchannelNative.CertContext));
                if (context.pbCertEncoded == IntPtr.Zero || context.cbCertEncoded == 0)
                {
                    SchannelNative.CertFreeCertificateContext(cert);
                    cert = IntPtr.Zero;
                    return IntPtr.Zero;
                }
                der = new byte[(int)context.cbCertEncoded];
                Marshal.Copy(context.pbCertEncoded, der, 0, (int)context.cbCertEncoded);
                return cert;
            }
            finally
            {
                if (hKey != IntPtr.Zero) try { SchannelNative.CryptDestroyKey(hKey); } catch { }
                if (nameBytes != IntPtr.Zero) Marshal.FreeHGlobal(nameBytes);
                if (oid != IntPtr.Zero) Marshal.FreeHGlobal(oid);
                if (keyInfo != IntPtr.Zero) Marshal.FreeHGlobal(keyInfo);
                if (algorithm != IntPtr.Zero) Marshal.FreeHGlobal(algorithm);
                if (start != IntPtr.Zero) Marshal.FreeHGlobal(start);
                if (end != IntPtr.Zero) Marshal.FreeHGlobal(end);
                if (container != IntPtr.Zero) Marshal.FreeHGlobal(container);
                if (provider != IntPtr.Zero) Marshal.FreeHGlobal(provider);
            }
        }

        private static bool SetKeyProviderInfo(IntPtr cert, IntPtr hProv, string providerName, uint providerType, uint providerFlags)
        {
            SchannelNative.CryptKeyProvInfo info = BuildKeyProviderInfo(providerName, providerType, providerFlags);
            IntPtr providerInfo = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SchannelNative.CryptKeyProvInfo)));
            IntPtr container = AllocString(ContainerName, true);
            IntPtr provider = string.IsNullOrEmpty(providerName) ? IntPtr.Zero : AllocString(providerName, true);
            try
            {
                info.pwszContainerName = container;
                info.pwszProvName = provider;
                Marshal.StructureToPtr(info, providerInfo, false);
                if (!SchannelNative.CertSetCertificateContextProperty(
                    cert, SchannelNative.CertKeyProvInfoPropId, 0, providerInfo))
                    return false;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(providerInfo);
                Marshal.FreeHGlobal(container);
                if (provider != IntPtr.Zero) Marshal.FreeHGlobal(provider);
            }
        }

        private static void ValidatePrivateKey(IntPtr cert)
        {
            IntPtr key;
            uint keySpec;
            bool callerFree;
            if (!SchannelNative.CryptAcquireCertificatePrivateKey(
                cert, 0, IntPtr.Zero, out key, out keySpec, out callerFree))
                throw new InvalidOperationException("TLS certificate has no usable private key (" + LastError() + ")");
            // For legacy CAPI keys CryptAcquireCertificatePrivateKey may return
            // a provider handle owned by the caller.  Do not release it here;
            // the identity's provider handle is kept alive for Schannel.
            if (callerFree && key != IntPtr.Zero)
            {
                try { SchannelNative.CryptReleaseContext(key, 0); } catch { }
            }
        }

        private static SchannelNative.CryptKeyProvInfo BuildKeyProviderInfo(string providerName, uint providerType, uint providerFlags)
        {
            SchannelNative.CryptKeyProvInfo info = new SchannelNative.CryptKeyProvInfo();
            info.dwProvType = providerType;
            info.dwFlags = providerFlags & SchannelNative.CryptMachineKeyset;
            info.dwKeySpec = SchannelNative.AtKeyExchange;
            return info;
        }

        private static IntPtr AllocString(string value, bool unicode)
        {
            byte[] bytes = unicode
                ? Encoding.Unicode.GetBytes((value ?? "") + "\0")
                : Encoding.ASCII.GetBytes((value ?? "") + "\0");
            IntPtr p = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            return p;
        }

        private static SchannelNative.SystemTime ToSystemTime(DateTime value)
        {
            SchannelNative.SystemTime t = new SchannelNative.SystemTime();
            t.wYear = (ushort)value.Year;
            t.wMonth = (ushort)value.Month;
            t.wDay = (ushort)value.Day;
            t.wHour = (ushort)value.Hour;
            t.wMinute = (ushort)value.Minute;
            t.wSecond = (ushort)value.Second;
            t.wMilliseconds = (ushort)value.Millisecond;
            return t;
        }

        private static void WriteCertificateAtomically(string path, byte[] der)
        {
            string temp = path + ".new";
            using (FileStream fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(der, 0, der.Length);
                fs.Flush();
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static string LastError()
        {
            try { return "0x" + Marshal.GetLastWin32Error().ToString("X8"); }
            catch { return "unknown"; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (CertificateContext != IntPtr.Zero)
            {
                try { SchannelNative.CertFreeCertificateContext(CertificateContext); } catch { }
                CertificateContext = IntPtr.Zero;
            }
            if (ProviderHandle != IntPtr.Zero)
            {
                try { SchannelNative.CryptReleaseContext(ProviderHandle, 0); } catch { }
                ProviderHandle = IntPtr.Zero;
            }
        }
    }
}
