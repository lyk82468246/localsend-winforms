using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Localsend.Backend.Runtime;

namespace Localsend.Backend.Tls
{
    /// <summary>
    /// Late-bound Positron ABI adapter.  No positron_tls.dll import is
    /// resolved until a CE device is actually selected by the router.
    ///
    /// ABI v2 owns its TCP sockets, so the endpoint contract is used for
    /// LocalSend HTTP.  The native connection is exposed to the HTTP parser
    /// only through PositronStream, which keeps the rest of the application
    /// independent of the ARM/VC DLL.
    /// </summary>
    internal sealed class PositronTlsProvider : ITlsProvider, ITlsEndpointProvider, IDisposable
    {
        private const int AbiV2 = 2;
        private const uint RequireClientCertificate = 0x0001u;
        private const int DefaultTimeoutMs = 15000;
        private const int FingerprintCapacity = 65;

        private readonly RuntimeEnvironmentInfo _environment;
        private readonly object _gate = new object();
        private IntPtr _identity;
        private bool _initialized;
        private bool _disposed;
        private bool _cleanupPending;
        private bool _cleaned;
        private bool _endpointReady;
        private int _activeConnections;
        private int _activeListeners;
        private string _fingerprint = "";
        private int _abi;

        public PositronTlsProvider(RuntimeEnvironmentInfo environment)
        { _environment = environment; }

        public string Name { get { return "positron"; } }

        // Positron ABI v2 does not wrap an existing NetworkStream.
        public bool SupportsStreamTransport { get { return false; } }

        public bool SupportsEndpointTransport
        {
            get
            {
                lock (_gate) { return _endpointReady && !_disposed && !_cleaned; }
            }
        }

        public string IdentityFingerprint
        {
            get { lock (_gate) { return _fingerprint; } }
        }

        public EncryptionCapabilityReport Probe()
        {
            lock (_gate)
            {
                if (_disposed) return Failed("tls.providerDisposed", "");
                if (_environment == null || !_environment.IsWindowsCe)
                    return Failed("tls.ceOnly", "");
            }

            try
            {
                int abi = Native.GetAbiVersion();
                if (abi < AbiV2)
                {
                    EnsureInitialized();
                    lock (_gate)
                    {
                        if (_disposed) return Failed("tls.providerDisposed", "");
                        _abi = abi;
                        _endpointReady = false;
                    }
                    return new EncryptionCapabilityReport(
                        EncryptionCapabilityLevel.ReceiveOnly, Name,
                        "tls.positronAbiLegacy", "ABI " + abi);
                }

                EnsureInitialized();
                EnsureIdentity();

                // Resolve every endpoint/read/write entry point without
                // opening a socket.  This turns a mixed-version DLL into a
                // normal capability report instead of a later CLR failure.
                VerifyEndpointAbi();
                lock (_gate)
                {
                    if (_disposed) return Failed("tls.providerDisposed", "");
                    _abi = abi;
                    _endpointReady = true;
                }
                return new EncryptionCapabilityReport(
                    EncryptionCapabilityLevel.Full, Name, "tls.ok",
                    "Positron TLS ABI " + abi + "; native socket endpoint");
            }
            catch (DllNotFoundException ex)
            { return FailedProbe("tls.positronDllMissing", ex.Message); }
            catch (BadImageFormatException ex)
            { return FailedProbe("tls.positronDllMissing", ex.Message); }
            catch (EntryPointNotFoundException ex)
            { return FailedProbe("tls.positronAbiInvalid", ex.Message); }
            catch (PositronInitException ex)
            { return FailedProbe("tls.positronInitFailed", ex.Message); }
            catch (Exception ex)
            { return FailedProbe("tls.probeFailed", ex.Message); }
        }

        // The stream-oriented interface is intentionally unsupported for
        // Positron.  Its socket is created inside PTls_ConnectPeer instead.
        public TlsSession Connect(Stream transport, string targetName, string expectedFingerprint)
        {
            throw new NotSupportedException(
                "Positron TLS ABI owns the CE socket; use ConnectPeer");
        }

        public TlsSession Accept(Stream transport)
        {
            throw new NotSupportedException(
                "Positron TLS ABI owns the CE socket; use Listen/Accept");
        }

        public ITlsEndpointListener Listen(int port, int handshakeTimeoutMs)
        {
            EnsureEndpointReady();
            if (port <= 0 || port > 65535)
                throw new ArgumentOutOfRangeException("port");
            int timeout = handshakeTimeoutMs > 0 ? handshakeTimeoutMs : DefaultTimeoutMs;

            IntPtr listener;
            lock (_gate)
            {
                ThrowIfUnavailableLocked();
                EnsureIdentityLocked();
                listener = Native.ServerListen(
                    _identity, port, RequireClientCertificate, timeout);
                if (listener == IntPtr.Zero)
                    throw new IOException("Positron TLS listener failed: " + LastError());
                _activeListeners++;
            }
            return new PositronListener(this, listener);
        }

        public TlsSession ConnectPeer(string host, int port,
            string expectedFingerprint, int timeoutMs)
        {
            EnsureEndpointReady();
            if (string.IsNullOrEmpty(host))
                throw new ArgumentNullException("host");
            if (port <= 0 || port > 65535)
                throw new ArgumentOutOfRangeException("port");
            int timeout = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;
            IntPtr connection = IntPtr.Zero;

            lock (_gate)
            {
                ThrowIfUnavailableLocked();
                EnsureIdentityLocked();
                byte[] expected = string.IsNullOrEmpty(expectedFingerprint)
                    ? null : Utf8Z(expectedFingerprint);
                connection = Native.ConnectPeer(
                    Utf8Z(host), port, _identity, expected, timeout);
                if (connection == IntPtr.Zero)
                    throw new IOException("Positron TLS connect failed: " + LastError());
                _activeConnections++;
            }

            try
            {
                string fingerprint = ReadPeerFingerprint(connection);
                PositronStream stream = new PositronStream(connection, this);
                TlsSession session = new TlsSession(stream, null, fingerprint,
                    "TLS 1.2", "Positron ABI v2");
                connection = IntPtr.Zero;
                return session;
            }
            catch
            {
                if (connection != IntPtr.Zero)
                {
                    try { Native.Close(connection); } catch { }
                }
                ReleaseConnection();
                throw;
            }
        }

        private void EnsureEndpointReady()
        {
            lock (_gate)
            {
                ThrowIfUnavailableLocked();
                if (!_endpointReady)
                    throw new InvalidOperationException(
                        "Positron TLS endpoint is not available");
            }
        }

        private void EnsureInitialized()
        {
            lock (_gate)
            {
                if (_initialized) return;
                ThrowIfUnavailableLocked();
                if (Native.Init() == 0)
                    throw new PositronInitException(
                        "PTls_Init failed: " + LastError());
                _initialized = true;
            }
        }

        private void EnsureIdentity()
        {
            lock (_gate) { EnsureIdentityLocked(); }
        }

        private void EnsureIdentityLocked()
        {
            if (_identity != IntPtr.Zero) return;
            ThrowIfUnavailableLocked();
            if (!_initialized)
            {
                if (Native.Init() == 0)
                    throw new PositronInitException(
                        "PTls_Init failed: " + LastError());
                _initialized = true;
            }

            string dir = @"\Application Data\LocalSend";
            string cert = dir + "\\identity-cert.pem";
            string key = dir + "\\identity-key.pem";
            try { Directory.CreateDirectory(dir); } catch { }
            IntPtr identity = Native.IdentityLoadOrCreate(Utf8Z(cert), Utf8Z(key));
            if (identity == IntPtr.Zero)
                throw new InvalidOperationException(
                    "PTls_IdentityLoadOrCreate failed: " + LastError());
            try
            {
                byte[] output = new byte[FingerprintCapacity];
                if (Native.IdentityFingerprint(identity, output, output.Length) == 0)
                    throw new InvalidOperationException(
                        "PTls_IdentityFingerprint failed: " + LastError());
                _fingerprint = Utf8FromZ(output);
                _identity = identity;
                identity = IntPtr.Zero;
            }
            finally
            {
                if (identity != IntPtr.Zero)
                {
                    try { Native.IdentityClose(identity); } catch { }
                }
            }
        }

        private static void VerifyEndpointAbi()
        {
            // Each call is guarded by a null/invalid-argument check in the
            // native implementation, so no network or identity is touched.
            Native.ServerClose(IntPtr.Zero);
            Native.Close(IntPtr.Zero);
            Native.ServerListen(IntPtr.Zero, 0, 0, 1);
            int remotePort = 0;
            Native.ServerAccept(IntPtr.Zero, null, 0, ref remotePort);
            Native.ConnectPeer(Utf8Z(""), 0, IntPtr.Zero, null, 1);
            Native.PeerFingerprint(IntPtr.Zero, new byte[FingerprintCapacity],
                FingerprintCapacity);
            Native.Read(IntPtr.Zero, new byte[1], 1);
            Native.Write(IntPtr.Zero, new byte[1], 1);
            Native.CopyLastError(new byte[256], 256);
        }

        private string ReadPeerFingerprint(IntPtr connection)
        {
            byte[] output = new byte[FingerprintCapacity];
            if (Native.PeerFingerprint(connection, output, output.Length) == 0)
                throw new InvalidOperationException(
                    "PTls_PeerFingerprint failed: " + LastError());
            return Utf8FromZ(output);
        }

        private static byte[] Utf8Z(string value)
        { return Encoding.UTF8.GetBytes((value ?? "") + "\0"); }

        private static string Utf8FromZ(byte[] value)
        {
            int length = 0;
            while (length < value.Length && value[length] != 0) length++;
            return Encoding.UTF8.GetString(value, 0, length);
        }

        private static string LastError()
        {
            try
            {
                byte[] output = new byte[256];
                int length = Native.CopyLastError(output, output.Length);
                if (length >= 0)
                {
                    string text = Utf8FromZ(output);
                    if (!string.IsNullOrEmpty(text)) return text;
                }
            }
            catch { }
            return "unknown Positron TLS error";
        }

        private void ThrowIfUnavailableLocked()
        {
            if (_disposed || _cleaned)
                throw new ObjectDisposedException("positron_tls");
        }

        private EncryptionCapabilityReport FailedProbe(string reason, string detail)
        {
            lock (_gate) { _endpointReady = false; }
            return Failed(reason, detail);
        }

        private static EncryptionCapabilityReport Failed(string reason, string detail)
        {
            return new EncryptionCapabilityReport(
                EncryptionCapabilityLevel.Unavailable, "positron", reason,
                detail ?? "");
        }

        private sealed class PositronInitException : InvalidOperationException
        {
            public PositronInitException(string message) : base(message) { }
        }

        internal void RegisterConnection()
        {
            lock (_gate)
            {
                // A listener keeps the native module alive while an accept
                // is completing, even if the UI has begun shutdown.
                if (_cleaned)
                    throw new ObjectDisposedException("positron_tls");
                _activeConnections++;
            }
        }

        internal void ReleaseConnection()
        {
            bool cleanup;
            lock (_gate)
            {
                if (_activeConnections > 0) _activeConnections--;
                cleanup = CanFinalizeCleanupLocked();
            }
            if (cleanup) FinalizeCleanup();
        }

        internal void ReleaseListener()
        {
            bool cleanup;
            lock (_gate)
            {
                if (_activeListeners > 0) _activeListeners--;
                cleanup = CanFinalizeCleanupLocked();
            }
            if (cleanup) FinalizeCleanup();
        }

        private bool CanFinalizeCleanupLocked()
        {
            return _cleanupPending && !_cleaned
                && _activeConnections == 0 && _activeListeners == 0;
        }

        private void FinalizeCleanup()
        {
            IntPtr identity;
            bool initialized;
            lock (_gate)
            {
                if (!CanFinalizeCleanupLocked()) return;
                _cleaned = true;
                identity = _identity;
                _identity = IntPtr.Zero;
                initialized = _initialized;
                _initialized = false;
            }
            if (identity != IntPtr.Zero)
            {
                try { Native.IdentityClose(identity); } catch { }
            }
            if (initialized)
            {
                try { Native.Cleanup(); } catch { }
            }
        }

        public void Dispose()
        {
            bool cleanup;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _endpointReady = false;
                _cleanupPending = true;
                cleanup = CanFinalizeCleanupLocked();
            }
            if (cleanup) FinalizeCleanup();
        }

        private sealed class PositronListener : ITlsEndpointListener
        {
            private readonly PositronTlsProvider _owner;
            private readonly object _gate = new object();
            private readonly ManualResetEvent _acceptDone =
                new ManualResetEvent(true);
            private IntPtr _handle;
            private bool _closing;
            private bool _disposed;

            public PositronListener(PositronTlsProvider owner, IntPtr handle)
            {
                _owner = owner;
                _handle = handle;
            }

            public TlsSession Accept(out IPEndPoint remote)
            {
                remote = null;
                IntPtr handle;
                lock (_gate)
                {
                    if (_disposed || _closing || _handle == IntPtr.Zero)
                        return null;
                    handle = _handle;
                    _acceptDone.Reset();
                }

                try
                {
                    byte[] remoteIp = new byte[16];
                    int remotePort = 0;
                    IntPtr connection = Native.ServerAccept(
                        handle, remoteIp, remoteIp.Length, ref remotePort);
                    if (connection == IntPtr.Zero)
                    {
                        lock (_gate)
                        {
                            if (_closing || _disposed) return null;
                        }
                        throw new IOException(
                            "Positron TLS accept failed: " + LastError());
                    }

                    try
                    {
                        _owner.RegisterConnection();
                        string ipText = Utf8FromZ(remoteIp);
                        IPAddress address;
                        try { address = IPAddress.Parse(ipText); }
                        catch { address = IPAddress.Any; }
                        remote = new IPEndPoint(address, remotePort);
                        string fingerprint = _owner.ReadPeerFingerprint(connection);
                        PositronStream stream = new PositronStream(connection, _owner);
                        TlsSession session = new TlsSession(stream, null, fingerprint,
                            "TLS 1.2", "Positron ABI v2");
                        connection = IntPtr.Zero;
                        return session;
                    }
                    catch
                    {
                        try { Native.Close(connection); } catch { }
                        _owner.ReleaseConnection();
                        throw;
                    }
                }
                finally
                {
                    try { _acceptDone.Set(); } catch { }
                }
            }

            public void Dispose()
            {
                IntPtr handle;
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    _closing = true;
                    handle = _handle;
                    _handle = IntPtr.Zero;
                }
                if (handle != IntPtr.Zero)
                {
                    try { Native.ServerClose(handle); } catch { }
                }
                try { _acceptDone.WaitOne(5000, false); } catch { }
                try { _acceptDone.Close(); } catch { }
                _owner.ReleaseListener();
            }
        }

        private sealed class PositronStream : Stream
        {
            private readonly PositronTlsProvider _owner;
            private readonly object _gate = new object();
            private IntPtr _connection;
            private bool _disposed;

            public PositronStream(IntPtr connection, PositronTlsProvider owner)
            {
                _connection = connection;
                _owner = owner;
            }

            public override bool CanRead { get { lock (_gate) { return !_disposed; } } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { lock (_gate) { return !_disposed; } } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public override void Flush() { ThrowIfDisposed(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException("buffer");
                if (offset < 0 || count < 0 || offset > buffer.Length - count)
                    throw new ArgumentOutOfRangeException();
                if (count == 0) return 0;
                lock (_gate)
                {
                    ThrowIfDisposedLocked();
                    byte[] target = Slice(buffer, offset, count);
                    int result = Native.Read(_connection, target, count);
                    if (result < 0)
                        throw new IOException("Positron TLS read failed: " + LastError());
                    if (target != buffer && result > 0)
                        Buffer.BlockCopy(target, 0, buffer, offset, result);
                    return result;
                }
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException("buffer");
                if (offset < 0 || count < 0 || offset > buffer.Length - count)
                    throw new ArgumentOutOfRangeException();
                if (count == 0) return;
                lock (_gate)
                {
                    ThrowIfDisposedLocked();
                    int result = Native.Write(
                        _connection, Slice(buffer, offset, count), count);
                    if (result < 0)
                        throw new IOException("Positron TLS write failed: " + LastError());
                    if (result != count)
                        throw new EndOfStreamException("Positron TLS write ended early");
                }
            }

            private static byte[] Slice(byte[] buffer, int offset, int count)
            {
                if (offset == 0 && count == buffer.Length) return buffer;
                byte[] copy = new byte[count];
                Buffer.BlockCopy(buffer, offset, copy, 0, count);
                return copy;
            }

            public override long Seek(long offset, SeekOrigin origin)
            { throw new NotSupportedException(); }

            public override void SetLength(long value)
            { throw new NotSupportedException(); }

            private void ThrowIfDisposed()
            {
                lock (_gate) { ThrowIfDisposedLocked(); }
            }

            private void ThrowIfDisposedLocked()
            {
                if (_disposed || _connection == IntPtr.Zero)
                    throw new ObjectDisposedException("Positron TLS stream");
            }

            protected override void Dispose(bool disposing)
            {
                IntPtr connection = IntPtr.Zero;
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    connection = _connection;
                    _connection = IntPtr.Zero;
                }
                if (connection != IntPtr.Zero)
                {
                    try { Native.Close(connection); } catch { }
                    _owner.ReleaseConnection();
                }
                base.Dispose(disposing);
            }
        }

        private static class Native
        {
            [DllImport("positron_tls.dll", EntryPoint = "PTls_GetAbiVersion")]
            internal static extern int GetAbiVersion();

            // Win32 BOOL is declared as a four-byte int for Compact Framework.
            [DllImport("positron_tls.dll", EntryPoint = "PTls_Init")]
            internal static extern int Init();

            [DllImport("positron_tls.dll", EntryPoint = "PTls_Cleanup")]
            internal static extern void Cleanup();

            [DllImport("positron_tls.dll", EntryPoint = "PTls_IdentityLoadOrCreate")]
            internal static extern IntPtr IdentityLoadOrCreate(
                byte[] certPathUtf8, byte[] keyPathUtf8);

            [DllImport("positron_tls.dll", EntryPoint = "PTls_IdentityFingerprint")]
            internal static extern int IdentityFingerprint(
                IntPtr identity, byte[] output, int capacity);

            [DllImport("positron_tls.dll", EntryPoint = "PTls_IdentityClose")]
            internal static extern void IdentityClose(IntPtr identity);

            [DllImport("positron_tls.dll", EntryPoint = "PTls_ServerListen")]
            internal static extern IntPtr ServerListen(
                IntPtr identity, int port, uint flags, int handshakeTimeoutMs);

            [DllImport("positron_tls.dll", EntryPoint = "PTls_ServerAccept")]
            internal static extern IntPtr ServerAccept(
                IntPtr listener, byte[] remoteIpUtf8, int remoteIpCapacity,
                ref int remotePort);

            [DllImport("positron_tls.dll", EntryPoint = "PTls_ServerClose")]
            internal static extern void ServerClose(IntPtr listener);

            [DllImport("positron_tls.dll", EntryPoint = "PTls_ConnectPeer")]
            internal static extern IntPtr ConnectPeer(
                byte[] hostUtf8, int port, IntPtr identity,
                byte[] expectedFingerprint, int timeoutMs);

            [DllImport("positron_tls.dll", EntryPoint = "PTls_PeerFingerprint")]
            internal static extern int PeerFingerprint(
                IntPtr connection, byte[] output, int capacity);

            [DllImport("positron_tls.dll", EntryPoint = "PTls_Write")]
            internal static extern int Write(IntPtr connection, byte[] buffer, int length);

            [DllImport("positron_tls.dll", EntryPoint = "PTls_Read")]
            internal static extern int Read(IntPtr connection, byte[] buffer, int length);

            [DllImport("positron_tls.dll", EntryPoint = "PTls_Close")]
            internal static extern void Close(IntPtr connection);

            [DllImport("positron_tls.dll", EntryPoint = "PTls_CopyLastError")]
            internal static extern int CopyLastError(byte[] output, int capacity);
        }
    }
}
