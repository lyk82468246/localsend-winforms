using System;
using System.IO;
using Localsend.Backend.Runtime;

namespace Localsend.Backend.Tls
{
    /// <summary>
    /// Transport-neutral TLS provider contract. Implementations own the
    /// handshake and certificate material; HTTP only sees a Stream.
    /// </summary>
    public interface ITlsProvider
    {
        string Name { get; }
        /// <summary>Whether this provider can wrap an existing TCP stream.</summary>
        bool SupportsStreamTransport { get; }
        EncryptionCapabilityReport Probe();
        TlsSession Connect(Stream transport, string targetName, string expectedFingerprint);
        TlsSession Accept(Stream transport);
    }

    /// <summary>Authenticated stream plus the identity learned during TLS.</summary>
    public sealed class TlsSession : IDisposable
    {
        private bool _disposed;

        public Stream Stream { get; private set; }
        public byte[] PeerCertificateDer { get; private set; }
        public string PeerFingerprint { get; private set; }
        public string Protocol { get; private set; }
        public string CipherSuite { get; private set; }

        public TlsSession(
            Stream stream,
            byte[] peerCertificateDer,
            string peerFingerprint,
            string protocol,
            string cipherSuite)
        {
            Stream = stream;
            PeerCertificateDer = peerCertificateDer;
            PeerFingerprint = peerFingerprint;
            Protocol = protocol ?? "";
            CipherSuite = cipherSuite ?? "";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (Stream != null) Stream.Dispose(); } catch { }
            Stream = null;
        }
    }

    /// <summary>
    /// Chooses a provider only after the platform has been identified. The
    /// concrete Schannel and Positron implementations can be added without
    /// making the HTTP or UI layers depend on either one.
    /// </summary>
    public sealed class TlsProviderRouter
    {
        private readonly RuntimeEnvironmentInfo _environment;
        private ITlsProvider _provider;
        private EncryptionCapabilityReport _report;

        public TlsProviderRouter(RuntimeEnvironmentInfo environment)
        {
            _environment = environment;
            _provider = environment != null && environment.IsWindowsCe
                ? (ITlsProvider)new PositronTlsProvider(environment)
                : (ITlsProvider)new SchannelTlsProvider(environment);
            // Probe through the guarded public method.  A provider must
            // never make the application constructor fail just because a
            // platform API, key container, or optional CE DLL is unavailable.
            _report = Probe();
        }

        public ITlsProvider Provider { get { return _provider; } }
        public EncryptionCapabilityReport Report { get { return _report; } }
        public RuntimeEnvironmentInfo RuntimeEnvironment { get { return _environment; } }
        public bool SupportsStreamTransport
        {
            get { return _provider != null && _provider.SupportsStreamTransport; }
        }

        /// <summary>Current LocalSend identity fingerprint, when the provider has one.</summary>
        public string IdentityFingerprint
        {
            get
            {
                SchannelTlsProvider schannel = _provider as SchannelTlsProvider;
                if (schannel != null)
                {
                    try { return schannel.IdentityFingerprint; } catch { return ""; }
                }
                PositronTlsProvider positron = _provider as PositronTlsProvider;
                if (positron != null) return positron.IdentityFingerprint;
                return "";
            }
        }

        public EncryptionCapabilityReport Probe()
        {
            try
            {
                _report = _provider.Probe();
            }
            catch (Exception ex)
            {
                _report = new EncryptionCapabilityReport(
                    EncryptionCapabilityLevel.Unavailable,
                    _provider.Name,
                    "tls.probeFailed",
                    ex.Message);
            }
            return _report;
        }

        public void Dispose()
        {
            SchannelTlsProvider schannel = _provider as SchannelTlsProvider;
            if (schannel != null) schannel.Dispose();
            PositronTlsProvider positron = _provider as PositronTlsProvider;
            if (positron != null) positron.Dispose();
        }
    }
}
