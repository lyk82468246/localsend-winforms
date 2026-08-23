using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Localsend.Backend.Runtime;

namespace Localsend.Backend.Tls
{
    /// <summary>
    /// Late-bound Positron ABI adapter.  No positron_tls.dll import is
    /// resolved until a CE device is actually selected by the router.
    /// </summary>
    internal sealed class PositronTlsProvider : ITlsProvider, IDisposable
    {
        private const int AbiV2 = 2;
        private readonly RuntimeEnvironmentInfo _environment;
        private IntPtr _identity;
        private bool _initialized;
        private bool _disposed;
        private string _fingerprint = "";

        public PositronTlsProvider(RuntimeEnvironmentInfo environment)
        { _environment = environment; }

        public string Name { get { return "positron"; } }
        public bool SupportsStreamTransport { get { return false; } }
        public string IdentityFingerprint { get { return _fingerprint; } }

        public EncryptionCapabilityReport Probe()
        {
            if (_disposed) return Failed("tls.providerDisposed", "");
            if (_environment == null || !_environment.IsWindowsCe)
                return Failed("tls.ceOnly", "");
            try
            {
                int abi = Native.GetAbiVersion();
                if (abi < AbiV2)
                {
                    if (!Native.Init()) return Failed("tls.positronInitFailed", "");
                    _initialized = true;
                    return new EncryptionCapabilityReport(
                        EncryptionCapabilityLevel.ReceiveOnly, Name,
                        "tls.positronAbiLegacy", "ABI " + abi);
                }
                EnsureInitialized();
                EnsureIdentity();
                return new EncryptionCapabilityReport(
                    EncryptionCapabilityLevel.Full, Name, "tls.ok",
                    "Positron TLS ABI " + abi);
            }
            catch (DllNotFoundException ex)
            { return Failed("tls.positronDllMissing", ex.Message); }
            catch (BadImageFormatException ex)
            { return Failed("tls.positronDllMissing", ex.Message); }
            catch (EntryPointNotFoundException ex)
            { return Failed("tls.positronAbiInvalid", ex.Message); }
            catch (Exception ex)
            { return Failed("tls.probeFailed", ex.Message); }
        }

        public TlsSession Connect(Stream transport, string targetName, string expectedFingerprint)
        {
            throw new NotSupportedException(
                "Positron owns the CE listener/socket in ABI v2; use its peer transport adapter");
        }

        public TlsSession Accept(Stream transport)
        {
            throw new NotSupportedException(
                "Positron owns the CE listener/socket in ABI v2; use its peer transport adapter");
        }

        private void EnsureInitialized()
        {
            if (_initialized) return;
            if (!Native.Init()) throw new InvalidOperationException("PTls_Init failed");
            _initialized = true;
        }

        private void EnsureIdentity()
        {
            if (_identity != IntPtr.Zero) return;
            string dir = @"\Application Data\LocalSend";
            string cert = dir + "\\identity-cert.pem";
            string key = dir + "\\identity-key.pem";
            _identity = Native.IdentityLoadOrCreate(Utf8Z(cert), Utf8Z(key));
            if (_identity == IntPtr.Zero) throw new InvalidOperationException("PTls_IdentityLoadOrCreate failed");
            byte[] output = new byte[65];
            if (!Native.IdentityFingerprint(_identity, output, output.Length))
                throw new InvalidOperationException("PTls_IdentityFingerprint failed");
            int length = 0;
            while (length < output.Length && output[length] != 0) length++;
            _fingerprint = Encoding.UTF8.GetString(output, 0, length);
        }

        private static byte[] Utf8Z(string value)
        { return Encoding.UTF8.GetBytes((value ?? "") + "\0"); }

        private static EncryptionCapabilityReport Failed(string reason, string detail)
        {
            return new EncryptionCapabilityReport(
                EncryptionCapabilityLevel.Unavailable, "positron", reason, detail ?? "");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_identity != IntPtr.Zero)
            {
                try { Native.IdentityClose(_identity); } catch { }
                _identity = IntPtr.Zero;
            }
            if (_initialized)
            {
                try { Native.Cleanup(); } catch { }
                _initialized = false;
            }
        }

        private static class Native
        {
            [DllImport("positron_tls.dll", EntryPoint = "PTls_GetAbiVersion")]
            internal static extern int GetAbiVersion();

            [DllImport("positron_tls.dll", EntryPoint = "PTls_Init")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool Init();

            [DllImport("positron_tls.dll", EntryPoint = "PTls_Cleanup")]
            internal static extern void Cleanup();

            [DllImport("positron_tls.dll", EntryPoint = "PTls_IdentityLoadOrCreate")]
            internal static extern IntPtr IdentityLoadOrCreate(byte[] certPathUtf8, byte[] keyPathUtf8);

            [DllImport("positron_tls.dll", EntryPoint = "PTls_IdentityFingerprint")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool IdentityFingerprint(IntPtr identity, byte[] output, int capacity);

            [DllImport("positron_tls.dll", EntryPoint = "PTls_IdentityClose")]
            internal static extern void IdentityClose(IntPtr identity);
        }
    }
}
