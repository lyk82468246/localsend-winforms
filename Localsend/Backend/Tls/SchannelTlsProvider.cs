using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Localsend.Backend.Runtime;
using Localsend.Backend.Util;

namespace Localsend.Backend.Tls
{
    /// <summary>
    /// Desktop TLS provider backed directly by Windows Schannel/SSPI.  It is
    /// deliberately stream-oriented: HTTP remains unaware of the native ABI.
    /// </summary>
    internal sealed class SchannelTlsProvider : ITlsProvider, IDisposable
    {
        private readonly RuntimeEnvironmentInfo _environment;
        private readonly object _lock = new object();
        private SchannelIdentity _identity;
        private bool _disposed;

        public SchannelTlsProvider(RuntimeEnvironmentInfo environment)
        { _environment = environment; }

        public string Name { get { return "schannel"; } }
        public bool SupportsStreamTransport { get { return true; } }

        public string IdentityFingerprint
        {
            get
            {
                EnsureIdentity();
                return _identity.Fingerprint;
            }
        }

        public EncryptionCapabilityReport Probe()
        {
            if (_disposed)
                return Failed("tls.providerDisposed", "");
            if (_environment != null && _environment.IsWindowsCe)
                return Failed("tls.desktopOnly", "");

            try
            {
                Log.Info("schannel probe: ensure identity");
                EnsureIdentity();
                Log.Info("schannel probe: mutual loopback");
                LoopbackResult mutual = TryLoopback(true);
                Log.Info("schannel probe: mutual result " + mutual.Success + " " + mutual.Detail);
                if (mutual.Success)
                    return new EncryptionCapabilityReport(
                        EncryptionCapabilityLevel.Full, Name, "tls.ok",
                        mutual.Detail);

                // A desktop Schannel that can serve TLS but cannot complete
                // the self-test with client certificates is useful as a
                // receive-only compatibility endpoint.  The caller still
                // refuses to advertise full LocalSend mTLS in this state.
                LoopbackResult serverOnly = TryLoopback(false);
                Log.Info("schannel probe: server-only result " + serverOnly.Success + " " + serverOnly.Detail);
                if (serverOnly.Success)
                    return new EncryptionCapabilityReport(
                        EncryptionCapabilityLevel.ReceiveOnly, Name,
                        "tls.mutualAuthUnavailable", mutual.Detail);

                return Failed("tls.handshakeFailed", mutual.Detail + "; " + serverOnly.Detail);
            }
            catch (DllNotFoundException ex)
            { return Failed("tls.systemApiMissing", ex.Message); }
            catch (EntryPointNotFoundException ex)
            { return Failed("tls.systemApiMissing", ex.Message); }
            catch (Exception ex)
            { return Failed("tls.probeFailed", ex.Message); }
        }

        public TlsSession Connect(Stream transport, string targetName, string expectedFingerprint)
        {
            if (transport == null) throw new ArgumentNullException("transport");
            EnsureIdentity();
            return EstablishClient(transport, targetName, expectedFingerprint);
        }

        public TlsSession Accept(Stream transport)
        {
            if (transport == null) throw new ArgumentNullException("transport");
            EnsureIdentity();
            return EstablishServer(transport, true);
        }

        private TlsSession EstablishClient(Stream transport, string targetName, string expectedFingerprint)
        {
            SchannelCredential credential = null;
            SchannelNative.SecHandle context = new SchannelNative.SecHandle();
            try
            {
                credential = AcquireCredential(false);
                HandshakeResult handshake = HandshakeClient(
                    transport, credential.Handle, targetName, ref context);
                SchannelPeer peer = QueryPeer(ref context);
                VerifyFingerprint(peer.Fingerprint, expectedFingerprint);
                SchannelStream stream = new SchannelStream(
                    transport, credential, context, handshake.Sizes,
                    handshake.Protocol, handshake.CipherSuite);
                credential = null;
                context = new SchannelNative.SecHandle();
                return new TlsSession(stream, peer.CertificateDer, peer.Fingerprint,
                    handshake.Protocol, handshake.CipherSuite);
            }
            catch
            {
                if (!context.IsZero) try { SchannelNative.DeleteSecurityContext(ref context); } catch { }
                if (credential != null) credential.Dispose();
                try { transport.Dispose(); } catch { }
                throw;
            }
        }

        private TlsSession EstablishServer(Stream transport, bool requireClientCertificate)
        {
            SchannelCredential credential = null;
            SchannelNative.SecHandle context = new SchannelNative.SecHandle();
            try
            {
                credential = AcquireCredential(true);
                HandshakeResult handshake = HandshakeServer(
                    transport, credential.Handle, ref context, requireClientCertificate);
                SchannelPeer peer = QueryPeer(ref context);
                if (requireClientCertificate && (peer.CertificateDer == null || peer.CertificateDer.Length == 0))
                    throw new InvalidOperationException("TLS peer did not provide a client certificate");
                SchannelStream stream = new SchannelStream(
                    transport, credential, context, handshake.Sizes,
                    handshake.Protocol, handshake.CipherSuite);
                credential = null;
                context = new SchannelNative.SecHandle();
                return new TlsSession(stream, peer.CertificateDer, peer.Fingerprint,
                    handshake.Protocol, handshake.CipherSuite);
            }
            catch
            {
                if (!context.IsZero) try { SchannelNative.DeleteSecurityContext(ref context); } catch { }
                if (credential != null) credential.Dispose();
                try { transport.Dispose(); } catch { }
                throw;
            }
        }

        private SchannelIdentity EnsureIdentity()
        {
            lock (_lock)
            {
                if (_identity == null)
                {
                    if (_disposed) throw new ObjectDisposedException("schannel");
                    _identity = SchannelIdentity.LoadOrCreate(_environment);
                }
                return _identity;
            }
        }

        private SchannelCredential AcquireCredential(bool server)
        {
            SchannelIdentity identity = EnsureIdentity();
            IntPtr certArray = Marshal.AllocHGlobal(IntPtr.Size);
            IntPtr authData = IntPtr.Zero;
            try
            {
                Marshal.WriteIntPtr(certArray, identity.CertificateContext);
                SchannelNative.SchannelCred cred = new SchannelNative.SchannelCred();
                cred.dwVersion = SchannelNative.SchannelCredVersion;
                cred.cCreds = 1;
                cred.paCred = certArray;
                cred.grbitEnabledProtocols = server
                    ? SchannelNative.SpProtTls12Server
                    : SchannelNative.SpProtTls12Client;
                // Keep credential acquisition permissive.  Certificate
                // pinning is performed after the handshake, which is the
                // only way to accept LocalSend's self-signed peer identity.
                cred.dwFlags = server
                    ? SchannelNative.SchCredNoSystemMapper
                    : SchannelNative.SchCredNoServerNameCheck
                    | SchannelNative.SchCredManualCredValidation
                    | SchannelNative.SchCredNoDefaultCreds;
                authData = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SchannelNative.SchannelCred)));
                Marshal.StructureToPtr(cred, authData, false);
                SchannelNative.SecHandle handle = new SchannelNative.SecHandle();
                int status = SchannelNative.AcquireCredentialsHandle(
                    null, SchannelNative.SecurityPackage,
                    server ? SchannelNative.SecPkgCredInbound : SchannelNative.SecPkgCredOutbound,
                    IntPtr.Zero, authData, IntPtr.Zero, IntPtr.Zero,
                    ref handle, IntPtr.Zero);
                if (status != SchannelNative.SecESuccess)
                    throw new InvalidOperationException("AcquireCredentialsHandle failed: " + Status(status));
                return new SchannelCredential(handle);
            }
            finally
            {
                if (authData != IntPtr.Zero) Marshal.FreeHGlobal(authData);
                Marshal.FreeHGlobal(certArray);
            }
        }

        private static HandshakeResult HandshakeClient(
            Stream transport, SchannelNative.SecHandle credential,
            string targetName, ref SchannelNative.SecHandle context)
        {
            uint req = SchannelNative.IscReqConfidentiality
                     | SchannelNative.IscReqStream
                     | SchannelNative.IscReqManualCredValidation;
            byte[] pending = new byte[0];
            bool first = true;
            while (true)
            {
                if (!first && pending.Length == 0) pending = ReadMore(transport, pending);
                IntPtr input = IntPtr.Zero;
                try
                {
                    if (!first) input = CreateInputDesc(pending);
                    OutputBuffers output = new OutputBuffers();
                    uint attrs;
                    IntPtr oldContext = IntPtr.Zero;
                    try
                    {
                        if (!first) oldContext = AllocHandle(context);
                        int status = SchannelNative.InitializeSecurityContext(
                            ref credential, oldContext, targetName,
                            req, 0, SchannelNative.SecurityNativeDrep,
                            input, 0, ref context, output.Descriptor,
                            out attrs, IntPtr.Zero);
                        if (status == SchannelNative.SecICompleteNeeded
                            || status == SchannelNative.SecICompleteAndContinue)
                        {
                            SchannelNative.CompleteAuthToken(ref context, output.Descriptor);
                            status = (status == SchannelNative.SecICompleteNeeded)
                                ? SchannelNative.SecESuccess : SchannelNative.SecIContinueNeeded;
                        }

                        byte[] token = output.ReadToken();
                        if (token.Length > 0) transport.Write(token, 0, token.Length);
                        if (status == SchannelNative.SecEIncompleteMessage)
                        {
                            pending = ReadMore(transport, pending);
                            continue;
                        }
                        pending = ExtractExtra(input, pending);
                        first = false;
                        if (status == SchannelNative.SecESuccess)
                            return ReadHandshakeResult(ref context);
                        if (status == SchannelNative.SecIContinueNeeded)
                            continue;
                        throw new InvalidOperationException("InitializeSecurityContext failed: " + Status(status));
                    }
                    finally
                    {
                        if (oldContext != IntPtr.Zero) Marshal.FreeHGlobal(oldContext);
                        output.Dispose();
                    }
                }
                finally
                { if (input != IntPtr.Zero) FreeInputDesc(input, pending); }
            }
        }

        private static HandshakeResult HandshakeServer(
            Stream transport, SchannelNative.SecHandle credential,
            ref SchannelNative.SecHandle context, bool requireClientCertificate)
        {
            uint req = SchannelNative.AscReqConfidentiality
                     | SchannelNative.AscReqStream;
            if (requireClientCertificate) req |= SchannelNative.AscReqMutualAuth;

            byte[] pending = new byte[0];
            bool first = true;
            while (true)
            {
                if (pending.Length == 0) pending = ReadMore(transport, pending);
                IntPtr input = CreateInputDesc(pending);
                OutputBuffers output = new OutputBuffers();
                IntPtr oldContext = IntPtr.Zero;
                try
                {
                    if (!first) oldContext = AllocHandle(context);
                    uint attrs;
                    int status = SchannelNative.AcceptSecurityContext(
                        ref credential, oldContext, input, req,
                        SchannelNative.SecurityNativeDrep, ref context,
                        output.Descriptor, out attrs, IntPtr.Zero);
                    if (status == SchannelNative.SecICompleteNeeded
                        || status == SchannelNative.SecICompleteAndContinue)
                    {
                        SchannelNative.CompleteAuthToken(ref context, output.Descriptor);
                        status = (status == SchannelNative.SecICompleteNeeded)
                            ? SchannelNative.SecESuccess : SchannelNative.SecIContinueNeeded;
                    }

                    byte[] token = output.ReadToken();
                    if (token.Length > 0) transport.Write(token, 0, token.Length);
                    if (status == SchannelNative.SecEIncompleteMessage)
                    {
                        pending = ReadMore(transport, pending);
                        continue;
                    }
                    pending = ExtractExtra(input, pending);
                    first = false;
                    if (status == SchannelNative.SecESuccess)
                        return ReadHandshakeResult(ref context);
                    if (status == SchannelNative.SecIContinueNeeded)
                        continue;
                    throw new InvalidOperationException("AcceptSecurityContext failed: " + Status(status));
                }
                finally
                {
                    if (oldContext != IntPtr.Zero) Marshal.FreeHGlobal(oldContext);
                    output.Dispose();
                    FreeInputDesc(input, pending);
                }
            }
        }

        private static HandshakeResult ReadHandshakeResult(ref SchannelNative.SecHandle context)
        {
            IntPtr sizesPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SchannelNative.StreamSizes)));
            try
            {
                int status = SchannelNative.QueryContextAttributes(
                    ref context, SchannelNative.SecPkgAttrStreamSizes, sizesPtr);
                if (status != SchannelNative.SecESuccess)
                    throw new InvalidOperationException("Query stream sizes failed: " + Status(status));
                SchannelNative.StreamSizes sizes = (SchannelNative.StreamSizes)
                    Marshal.PtrToStructure(sizesPtr, typeof(SchannelNative.StreamSizes));
                SchannelNative.ConnectionInfo info = QueryConnectionInfo(ref context);
                HandshakeResult result = new HandshakeResult();
                result.Sizes = sizes;
                result.Protocol = ProtocolName(info.dwProtocol);
                result.CipherSuite = CipherName(info.aiCipher, info.dwCipherStrength);
                return result;
            }
            finally { Marshal.FreeHGlobal(sizesPtr); }
        }

        private static SchannelNative.ConnectionInfo QueryConnectionInfo(ref SchannelNative.SecHandle context)
        {
            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SchannelNative.ConnectionInfo)));
            try
            {
                int status = SchannelNative.QueryContextAttributes(
                    ref context, SchannelNative.SecPkgAttrConnectionInfo, ptr);
                if (status != SchannelNative.SecESuccess) return new SchannelNative.ConnectionInfo();
                return (SchannelNative.ConnectionInfo)Marshal.PtrToStructure(
                    ptr, typeof(SchannelNative.ConnectionInfo));
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        private static SchannelPeer QueryPeer(ref SchannelNative.SecHandle context)
        {
            IntPtr certPtr = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(certPtr, IntPtr.Zero);
                int status = SchannelNative.QueryContextAttributes(
                    ref context, SchannelNative.SecPkgAttrRemoteCertContext, certPtr);
                if (status != SchannelNative.SecESuccess)
                    return new SchannelPeer(null, "");
                IntPtr cert = Marshal.ReadIntPtr(certPtr);
                if (cert == IntPtr.Zero) return new SchannelPeer(null, "");
                try
                {
                    SchannelNative.CertContext cc = (SchannelNative.CertContext)
                        Marshal.PtrToStructure(cert, typeof(SchannelNative.CertContext));
                    if (cc.pbCertEncoded == IntPtr.Zero || cc.cbCertEncoded == 0)
                        return new SchannelPeer(null, "");
                    byte[] der = new byte[(int)cc.cbCertEncoded];
                    Marshal.Copy(cc.pbCertEncoded, der, 0, der.Length);
                    return new SchannelPeer(der, Sha256.ToUpperHex(Sha256.Compute(der)));
                }
                finally { SchannelNative.CertFreeCertificateContext(cert); }
            }
            finally { Marshal.FreeHGlobal(certPtr); }
        }

        private static void VerifyFingerprint(string actual, string expected)
        {
            if (string.IsNullOrEmpty(expected)) return;
            string want = expected.Trim().ToUpper();
            if (want.Length != 64 || actual != want)
                throw new InvalidOperationException("TLS peer certificate fingerprint mismatch");
        }

        private LoopbackResult TryLoopback(bool mutual)
        {
            TcpListener listener = null;
            TcpClient client = null;
            LoopbackResult serverResult = new LoopbackResult();
            Thread serverThread = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                serverThread = new Thread(delegate()
                {
                    TcpClient accepted = null;
                    try
                    {
                        accepted = listener.AcceptTcpClient();
                        using (TlsSession session = EstablishServer(accepted.GetStream(), mutual))
                        {
                            byte[] ping = new byte[4];
                            ReadFully(session.Stream, ping, 0, ping.Length);
                            session.Stream.Write(ping, 0, ping.Length);
                            session.Stream.Flush();
                            serverResult.Detail = session.Protocol + " / " + session.CipherSuite;
                            serverResult.Success = true;
                        }
                    }
                    catch (Exception ex) { serverResult.Detail = ex.Message; }
                    finally { if (accepted != null) try { accepted.Close(); } catch { } }
                });
                serverThread.IsBackground = true;
                serverThread.Start();

                client = new TcpClient();
                client.Connect(IPAddress.Loopback, port);
                using (TlsSession session = EstablishClient(client.GetStream(), "localhost", ""))
                {
                    byte[] ping = Encoding.ASCII.GetBytes("ping");
                    session.Stream.Write(ping, 0, ping.Length);
                    session.Stream.Flush();
                    byte[] echo = new byte[4];
                    ReadFully(session.Stream, echo, 0, echo.Length);
                    if (Encoding.ASCII.GetString(echo, 0, echo.Length) != "ping")
                        throw new InvalidOperationException("TLS loopback payload mismatch");
                }
                if (serverThread != null) serverThread.Join(5000);
                if (!serverResult.Success && string.IsNullOrEmpty(serverResult.Detail))
                    serverResult.Detail = "TLS loopback server did not finish";
                return serverResult;
            }
            catch (Exception ex)
            {
                if (string.IsNullOrEmpty(serverResult.Detail)) serverResult.Detail = ex.Message;
                return serverResult;
            }
            finally
            {
                try { if (client != null) client.Close(); } catch { }
                try { if (listener != null) listener.Stop(); } catch { }
            }
        }

        private static byte[] ReadMore(Stream stream, byte[] existing)
        {
            byte[] temp = new byte[16 * 1024];
            int n = stream.Read(temp, 0, temp.Length);
            if (n <= 0) throw new EndOfStreamException("TLS peer closed during handshake");
            byte[] result = new byte[existing.Length + n];
            if (existing.Length > 0) Buffer.BlockCopy(existing, 0, result, 0, existing.Length);
            Buffer.BlockCopy(temp, 0, result, existing.Length, n);
            return result;
        }

        private static void ReadFully(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int n = stream.Read(buffer, offset, count);
                if (n <= 0) throw new EndOfStreamException("TLS peer closed");
                offset += n;
                count -= n;
            }
        }

        private static IntPtr CreateInputDesc(byte[] data)
        {
            IntPtr dataPtr = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, dataPtr, data.Length);
            SchannelNative.SecBuffer token = new SchannelNative.SecBuffer();
            token.cbBuffer = (uint)data.Length;
            token.BufferType = SchannelNative.SecBufferToken;
            token.pvBuffer = dataPtr;
            SchannelNative.SecBuffer empty = new SchannelNative.SecBuffer();
            empty.BufferType = SchannelNative.SecBufferEmpty;
            IntPtr buffers = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SchannelNative.SecBuffer)) * 2);
            Marshal.StructureToPtr(token, buffers, false);
            Marshal.StructureToPtr(empty, Add(buffers, Marshal.SizeOf(typeof(SchannelNative.SecBuffer))), false);
            SchannelNative.SecBufferDesc desc = new SchannelNative.SecBufferDesc();
            desc.ulVersion = SchannelNative.SecBufferVersion;
            desc.cBuffers = 2;
            desc.pBuffers = buffers;
            int descSize = Marshal.SizeOf(typeof(SchannelNative.SecBufferDesc));
            // Store the allocation owners immediately after the descriptor;
            // the pointer returned to SSPI still begins with SecBufferDesc.
            IntPtr holder = Marshal.AllocHGlobal(descSize + IntPtr.Size * 2);
            Marshal.StructureToPtr(desc, holder, false);
            Marshal.WriteIntPtr(Add(holder, descSize), dataPtr);
            Marshal.WriteIntPtr(Add(holder, descSize + IntPtr.Size), buffers);
            return holder;
        }

        private static void FreeInputDesc(IntPtr holder, byte[] ignored)
        {
            if (holder == IntPtr.Zero) return;
            int descSize = Marshal.SizeOf(typeof(SchannelNative.SecBufferDesc));
            IntPtr data = Marshal.ReadIntPtr(Add(holder, descSize));
            IntPtr buffers = Marshal.ReadIntPtr(Add(holder, descSize + IntPtr.Size));
            if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
            if (buffers != IntPtr.Zero) Marshal.FreeHGlobal(buffers);
            Marshal.FreeHGlobal(holder);
        }

        private static byte[] ExtractExtra(IntPtr holder, byte[] original)
        {
            if (holder == IntPtr.Zero || original == null || original.Length == 0) return new byte[0];
            int descSize = Marshal.SizeOf(typeof(SchannelNative.SecBufferDesc));
            IntPtr data = Marshal.ReadIntPtr(Add(holder, descSize));
            IntPtr buffers = Marshal.ReadIntPtr(Add(holder, descSize + IntPtr.Size));
            int size = Marshal.SizeOf(typeof(SchannelNative.SecBuffer));
            for (int i = 0; i < 2; i++)
            {
                SchannelNative.SecBuffer b = (SchannelNative.SecBuffer)Marshal.PtrToStructure(
                    Add(buffers, i * size), typeof(SchannelNative.SecBuffer));
                if (b.BufferType == SchannelNative.SecBufferExtra && b.cbBuffer > 0 && b.pvBuffer != IntPtr.Zero)
                {
                    Int64 start = b.pvBuffer.ToInt64() - data.ToInt64();
                    if (start >= 0 && start <= original.Length && start + b.cbBuffer <= original.Length)
                    {
                        byte[] extra = new byte[(int)b.cbBuffer];
                        Buffer.BlockCopy(original, (int)start, extra, 0, extra.Length);
                        return extra;
                    }
                }
            }
            return new byte[0];
        }

        private static IntPtr AllocHandle(SchannelNative.SecHandle handle)
        {
            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SchannelNative.SecHandle)));
            Marshal.StructureToPtr(handle, ptr, false);
            return ptr;
        }

        private static IntPtr Add(IntPtr ptr, int offset)
        { return new IntPtr(ptr.ToInt64() + offset); }

        private static string ProtocolName(uint value)
        {
            if ((value & SchannelNative.SpProtTls12Client) != 0
                || (value & SchannelNative.SpProtTls12Server) != 0) return "TLS 1.2";
            return "Schannel 0x" + value.ToString("X8");
        }

        private static string CipherName(uint alg, uint strength)
        {
            return "ALG 0x" + alg.ToString("X") + " (" + strength + " bit)";
        }

        private static string Status(int status)
        { return "0x" + status.ToString("X8"); }

        private static EncryptionCapabilityReport Failed(string reason, string detail)
        {
            return new EncryptionCapabilityReport(
                EncryptionCapabilityLevel.Unavailable, "schannel", reason, detail ?? "");
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                if (_identity != null) { _identity.Dispose(); _identity = null; }
            }
        }

        private sealed class SchannelCredential : IDisposable
        {
            public SchannelNative.SecHandle Handle;
            private bool _disposed;
            public SchannelCredential(SchannelNative.SecHandle handle) { Handle = handle; }
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                try { SchannelNative.FreeCredentialsHandle(ref Handle); } catch { }
            }
        }

        private sealed class SchannelPeer
        {
            public byte[] CertificateDer;
            public string Fingerprint;
            public SchannelPeer(byte[] der, string fingerprint)
            { CertificateDer = der; Fingerprint = fingerprint ?? ""; }
        }

        private sealed class HandshakeResult
        {
            public SchannelNative.StreamSizes Sizes;
            public string Protocol;
            public string CipherSuite;
        }

        private sealed class LoopbackResult
        {
            public bool Success;
            public string Detail;
        }

        private sealed class OutputBuffers : IDisposable
        {
            private readonly IntPtr _token;
            private readonly IntPtr _buffers;
            private bool _disposed;
            public IntPtr Descriptor { get; private set; }

            public OutputBuffers()
            {
                _token = Marshal.AllocHGlobal(64 * 1024);
                _buffers = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SchannelNative.SecBuffer)));
                SchannelNative.SecBuffer token = new SchannelNative.SecBuffer();
                token.cbBuffer = 64 * 1024;
                token.BufferType = SchannelNative.SecBufferToken;
                token.pvBuffer = _token;
                Marshal.StructureToPtr(token, _buffers, false);
                SchannelNative.SecBufferDesc desc = new SchannelNative.SecBufferDesc();
                desc.ulVersion = SchannelNative.SecBufferVersion;
                desc.cBuffers = 1;
                desc.pBuffers = _buffers;
                Descriptor = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SchannelNative.SecBufferDesc)));
                Marshal.StructureToPtr(desc, Descriptor, false);
            }

            public byte[] ReadToken()
            {
                if (Descriptor == IntPtr.Zero) return new byte[0];
                SchannelNative.SecBufferDesc desc = (SchannelNative.SecBufferDesc)
                    Marshal.PtrToStructure(Descriptor, typeof(SchannelNative.SecBufferDesc));
                SchannelNative.SecBuffer token = (SchannelNative.SecBuffer)
                    Marshal.PtrToStructure(desc.pBuffers, typeof(SchannelNative.SecBuffer));
                if (token.cbBuffer == 0 || token.pvBuffer == IntPtr.Zero) return new byte[0];
                byte[] output = new byte[(int)token.cbBuffer];
                Marshal.Copy(token.pvBuffer, output, 0, output.Length);
                return output;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (Descriptor != IntPtr.Zero) Marshal.FreeHGlobal(Descriptor);
                if (_buffers != IntPtr.Zero) Marshal.FreeHGlobal(_buffers);
                if (_token != IntPtr.Zero) Marshal.FreeHGlobal(_token);
                Descriptor = IntPtr.Zero;
            }
        }
    }

    /// <summary>Encrypted byte stream implementing the Schannel stream mode.</summary>
    internal sealed class SchannelStream : Stream
    {
        private readonly Stream _inner;
        private SchannelNative.SecHandle _context;
        private readonly object _lock = new object();
        private readonly SchannelNative.StreamSizes _sizes;
        private readonly string _protocol;
        private readonly string _cipher;
        private byte[] _encrypted = new byte[0];
        private byte[] _plain = new byte[0];
        private bool _disposed;
        private readonly IDisposable _credential;

        internal SchannelStream(Stream inner, IDisposable credential,
            SchannelNative.SecHandle context, SchannelNative.StreamSizes sizes,
            string protocol, string cipher)
        {
            _inner = inner;
            _credential = credential;
            _context = context;
            _sizes = sizes;
            _protocol = protocol;
            _cipher = cipher;
        }

        public string Protocol { get { return _protocol; } }
        public string CipherSuite { get { return _cipher; } }
        public override bool CanRead { get { return !_disposed; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return !_disposed; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { _inner.Flush(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (count == 0) return 0;
            lock (_lock)
            {
                ThrowIfDisposed();
                if (_plain.Length > 0)
                {
                    int n = Math.Min(count, _plain.Length);
                    Buffer.BlockCopy(_plain, 0, buffer, offset, n);
                    ConsumePlain(n);
                    return n;
                }
                FillPlain();
                if (_plain.Length == 0) return 0;
                int take = Math.Min(count, _plain.Length);
                Buffer.BlockCopy(_plain, 0, buffer, offset, take);
                ConsumePlain(take);
                return take;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            lock (_lock)
            {
                ThrowIfDisposed();
                int max = _sizes.cbMaximumMessage > 0 ? (int)_sizes.cbMaximumMessage : 16 * 1024;
                while (count > 0)
                {
                    int chunk = Math.Min(count, max);
                    WriteEncrypted(buffer, offset, chunk);
                    offset += chunk;
                    count -= chunk;
                }
            }
        }

        private void WriteEncrypted(byte[] plain, int offset, int count)
        {
            int header = (int)_sizes.cbHeader;
            int trailer = (int)_sizes.cbTrailer;
            int message = Math.Max(count, (int)_sizes.cbMaximumMessage);
            IntPtr memory = Marshal.AllocHGlobal(header + message + trailer);
            IntPtr buffers = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SchannelNative.SecBuffer)) * 3);
            IntPtr desc = IntPtr.Zero;
            try
            {
                IntPtr data = Add(memory, header);
                Marshal.Copy(plain, offset, data, count);
                SchannelNative.SecBuffer b0 = new SchannelNative.SecBuffer();
                b0.cbBuffer = (uint)header; b0.BufferType = SchannelNative.SecBufferStreamHeader; b0.pvBuffer = memory;
                SchannelNative.SecBuffer b1 = new SchannelNative.SecBuffer();
                b1.cbBuffer = (uint)count; b1.BufferType = SchannelNative.SecBufferData; b1.pvBuffer = data;
                SchannelNative.SecBuffer b2 = new SchannelNative.SecBuffer();
                b2.cbBuffer = (uint)trailer; b2.BufferType = SchannelNative.SecBufferStreamTrailer; b2.pvBuffer = Add(data, message);
                int sz = Marshal.SizeOf(typeof(SchannelNative.SecBuffer));
                Marshal.StructureToPtr(b0, buffers, false);
                Marshal.StructureToPtr(b1, Add(buffers, sz), false);
                Marshal.StructureToPtr(b2, Add(buffers, sz * 2), false);
                SchannelNative.SecBufferDesc bd = new SchannelNative.SecBufferDesc();
                bd.ulVersion = SchannelNative.SecBufferVersion; bd.cBuffers = 3; bd.pBuffers = buffers;
                desc = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SchannelNative.SecBufferDesc)));
                Marshal.StructureToPtr(bd, desc, false);
                int status = SchannelNative.EncryptMessage(ref _context, 0, desc, 0);
                if (status != SchannelNative.SecESuccess)
                    throw new IOException("EncryptMessage failed: 0x" + status.ToString("X8"));
                for (int i = 0; i < 3; i++)
                {
                    SchannelNative.SecBuffer b = (SchannelNative.SecBuffer)Marshal.PtrToStructure(
                        Add(buffers, sz * i), typeof(SchannelNative.SecBuffer));
                    if (b.cbBuffer > 0) WriteFully(_inner, b.pvBuffer, (int)b.cbBuffer);
                }
                _inner.Flush();
            }
            finally
            {
                if (desc != IntPtr.Zero) Marshal.FreeHGlobal(desc);
                Marshal.FreeHGlobal(buffers);
                Marshal.FreeHGlobal(memory);
            }
        }

        private void FillPlain()
        {
            while (_plain.Length == 0)
            {
                if (_encrypted.Length == 0)
                {
                    byte[] temp = new byte[16 * 1024];
                    int n = _inner.Read(temp, 0, temp.Length);
                    if (n <= 0) return;
                    _encrypted = new byte[n];
                    Buffer.BlockCopy(temp, 0, _encrypted, 0, n);
                }

                IntPtr memory = Marshal.AllocHGlobal(_encrypted.Length);
                IntPtr buffers = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SchannelNative.SecBuffer)) * 4);
                IntPtr desc = IntPtr.Zero;
                try
                {
                    Marshal.Copy(_encrypted, 0, memory, _encrypted.Length);
                    int sz = Marshal.SizeOf(typeof(SchannelNative.SecBuffer));
                    SchannelNative.SecBuffer first = new SchannelNative.SecBuffer();
                    first.cbBuffer = (uint)_encrypted.Length; first.BufferType = SchannelNative.SecBufferData; first.pvBuffer = memory;
                    Marshal.StructureToPtr(first, buffers, false);
                    for (int i = 1; i < 4; i++) Marshal.StructureToPtr(new SchannelNative.SecBuffer(), Add(buffers, sz * i), false);
                    SchannelNative.SecBufferDesc bd = new SchannelNative.SecBufferDesc();
                    bd.ulVersion = SchannelNative.SecBufferVersion; bd.cBuffers = 4; bd.pBuffers = buffers;
                    desc = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SchannelNative.SecBufferDesc)));
                    Marshal.StructureToPtr(bd, desc, false);
                    int status = SchannelNative.DecryptMessage(ref _context, desc, 0, IntPtr.Zero);
                    if (status == SchannelNative.SecEIncompleteMessage)
                    {
                        ReadEncryptedMore();
                        continue;
                    }
                    if (status == SchannelNative.SecIContextExpired || status == SchannelNative.SecEContextExpired)
                    {
                        _encrypted = new byte[0];
                        return;
                    }
                    if (status != SchannelNative.SecESuccess)
                        throw new IOException("DecryptMessage failed: 0x" + status.ToString("X8"));

                    byte[] extra = new byte[0];
                    byte[] plain = new byte[0];
                    for (int i = 0; i < 4; i++)
                    {
                        SchannelNative.SecBuffer b = (SchannelNative.SecBuffer)Marshal.PtrToStructure(
                            Add(buffers, sz * i), typeof(SchannelNative.SecBuffer));
                        if (b.BufferType == SchannelNative.SecBufferData && b.cbBuffer > 0 && b.pvBuffer != IntPtr.Zero)
                        {
                            plain = new byte[(int)b.cbBuffer];
                            Marshal.Copy(b.pvBuffer, plain, 0, plain.Length);
                        }
                        if (b.BufferType == SchannelNative.SecBufferExtra && b.cbBuffer > 0 && b.pvBuffer != IntPtr.Zero)
                        {
                            long start = b.pvBuffer.ToInt64() - memory.ToInt64();
                            if (start >= 0 && start + b.cbBuffer <= _encrypted.Length)
                            {
                                extra = new byte[(int)b.cbBuffer];
                                Buffer.BlockCopy(_encrypted, (int)start, extra, 0, extra.Length);
                            }
                        }
                    }
                    _encrypted = extra;
                    _plain = plain;
                }
                finally
                {
                    if (desc != IntPtr.Zero) Marshal.FreeHGlobal(desc);
                    Marshal.FreeHGlobal(buffers);
                    Marshal.FreeHGlobal(memory);
                }
            }
        }

        private void ReadEncryptedMore()
        {
            byte[] temp = new byte[16 * 1024];
            int n = _inner.Read(temp, 0, temp.Length);
            if (n <= 0) throw new EndOfStreamException("TLS peer closed");
            byte[] merged = new byte[_encrypted.Length + n];
            Buffer.BlockCopy(_encrypted, 0, merged, 0, _encrypted.Length);
            Buffer.BlockCopy(temp, 0, merged, _encrypted.Length, n);
            _encrypted = merged;
        }

        private void ConsumePlain(int count)
        {
            if (count >= _plain.Length) { _plain = new byte[0]; return; }
            byte[] rest = new byte[_plain.Length - count];
            Buffer.BlockCopy(_plain, count, rest, 0, rest.Length);
            _plain = rest;
        }

        private void ThrowIfDisposed()
        { if (_disposed) throw new ObjectDisposedException("SchannelStream"); }

        protected override void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                try { SchannelNative.DeleteSecurityContext(ref _context); } catch { }
                try { if (_credential != null) _credential.Dispose(); } catch { }
                try { if (disposing) _inner.Dispose(); } catch { }
                base.Dispose(disposing);
            }
        }

        private static IntPtr Add(IntPtr ptr, int offset)
        { return new IntPtr(ptr.ToInt64() + offset); }

        private static void WriteFully(Stream stream, IntPtr ptr, int count)
        {
            byte[] buffer = new byte[Math.Min(count, 16 * 1024)];
            int copied = 0;
            while (copied < count)
            {
                int n = Math.Min(buffer.Length, count - copied);
                Marshal.Copy(Add(ptr, copied), buffer, 0, n);
                stream.Write(buffer, 0, n);
                copied += n;
            }
        }
    }
}
