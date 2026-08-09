using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace IoFtp.Core.Transport;

/// <summary>
/// OpenSSL-backed TLS stream used only when Windows Schannel cannot agree on a
/// cipher with a legacy-compatible FTP server (notably some glFTPD setups).
/// </summary>
internal sealed class OpenSslTlsStream : Stream
{
    private const string LibSsl = "libssl-3-x64";
    private const string LibCrypto = "libcrypto-3-x64";
    private const int Tls12Version = 0x0303;
    private const int SslCtrlSetMinProtoVersion = 123;
    private const int SslCtrlSetMaxProtoVersion = 124;
    private const int SslCtrlSetTlsExtHostName = 55;
    private const int TlsextNameTypeHostName = 0;
    private const int SslErrorZeroReturn = 6;

    private readonly Stream _transport;
    private readonly IntPtr _context;
    private readonly IntPtr _ssl;
    private bool _disposed;

    private OpenSslTlsStream(Stream transport, IntPtr context, IntPtr ssl,
        string protocol, string cipher, int cipherBits, X509Certificate2? certificate)
    {
        _transport = transport;
        _context = context;
        _ssl = ssl;
        Protocol = protocol;
        Cipher = cipher;
        CipherBits = cipherBits;
        RemoteCertificate = certificate;
    }

    public string Protocol { get; }
    public string Cipher { get; }
    public int CipherBits { get; }
    public X509Certificate2? RemoteCertificate { get; }

    public static Task<OpenSslTlsStream> AuthenticateAsync(Socket socket, Stream transport,
        string targetHost, bool allowInvalidCertificate, CancellationToken cancellationToken) =>
        Task.Run(() => Authenticate(socket, transport, targetHost, allowInvalidCertificate, cancellationToken), cancellationToken);

    private static OpenSslTlsStream Authenticate(Socket socket, Stream transport, string targetHost,
        bool allowInvalidCertificate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Native.OPENSSL_init_ssl(0, IntPtr.Zero);
        var method = Native.TLS_client_method();
        var context = Native.SSL_CTX_new(method);
        if (context == IntPtr.Zero) throw CreateException("could not create an SSL context");

        IntPtr ssl = IntPtr.Zero;
        try
        {
            // Keep TLS 1.0/1.1 disabled. SECLEVEL=1 broadens TLS 1.2 cipher
            // interoperability without enabling anonymous, export or null ciphers.
            Native.SSL_CTX_ctrl(context, SslCtrlSetMinProtoVersion, Tls12Version, IntPtr.Zero);
            if (Native.SSL_CTX_set_cipher_list(context,
                    "DEFAULT:@SECLEVEL=1") != 1)
                throw CreateException("could not configure the compatibility cipher list");

            ssl = Native.SSL_new(context);
            if (ssl == IntPtr.Zero) throw CreateException("could not create an SSL session");

            if (!IPAddress.TryParse(targetHost, out _))
            {
                var host = Marshal.StringToHGlobalAnsi(targetHost);
                try { Native.SSL_ctrl(ssl, SslCtrlSetTlsExtHostName, TlsextNameTypeHostName, host); }
                finally { Marshal.FreeHGlobal(host); }
            }

            var socketValue = unchecked((int)socket.Handle.ToInt64());
            if (Native.SSL_set_fd(ssl, socketValue) != 1)
                throw CreateException("could not attach the network socket");
            if (Native.SSL_connect(ssl) != 1)
                throw CreateException("TLS handshake failed");

            cancellationToken.ThrowIfCancellationRequested();
            var certificate = ReadPeerCertificate(ssl);
            if (!allowInvalidCertificate)
                ValidateCertificate(certificate, targetHost);

            var cipherHandle = Native.SSL_get_current_cipher(ssl);
            var bits = Native.SSL_CIPHER_get_bits(cipherHandle, out _);
            var cipher = Marshal.PtrToStringAnsi(Native.SSL_CIPHER_get_name(cipherHandle)) ?? "unknown";
            var protocol = Marshal.PtrToStringAnsi(Native.SSL_get_version(ssl)) ?? "TLS";
            return new OpenSslTlsStream(transport, context, ssl, protocol, cipher, bits, certificate);
        }
        catch
        {
            if (ssl != IntPtr.Zero) Native.SSL_free(ssl);
            Native.SSL_CTX_free(context);
            throw;
        }
    }

    private static X509Certificate2? ReadPeerCertificate(IntPtr ssl)
    {
        var nativeCertificate = Native.SSL_get1_peer_certificate(ssl);
        if (nativeCertificate == IntPtr.Zero) return null;
        try
        {
            var length = Native.i2d_X509_length(nativeCertificate, IntPtr.Zero);
            if (length <= 0) return null;
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                var cursor = buffer;
                if (Native.i2d_X509(nativeCertificate, ref cursor) != length) return null;
                var encoded = new byte[length];
                Marshal.Copy(buffer, encoded, 0, length);
                return new X509Certificate2(encoded);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { Native.X509_free(nativeCertificate); }
    }

    private static void ValidateCertificate(X509Certificate2? certificate, string targetHost)
    {
        if (certificate is null) throw new AuthenticationException("OpenSSL server did not provide a certificate.");
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (!chain.Build(certificate))
            throw new AuthenticationException("OpenSSL certificate chain validation failed.");
        if (!certificate.MatchesHostname(targetHost, allowWildcards: true, allowCommonName: true))
            throw new AuthenticationException($"OpenSSL certificate name does not match {targetHost}.");
    }

    private static AuthenticationException CreateException(string operation)
    {
        var error = Native.ERR_get_error();
        var buffer = new byte[512];
        if (error != 0) Native.ERR_error_string_n(error, buffer, (nuint)buffer.Length);
        var detail = error == 0 ? "unknown OpenSSL error" : System.Text.Encoding.ASCII.GetString(buffer).TrimEnd('\0');
        return new AuthenticationException($"OpenSSL {operation}: {detail}");
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        unsafe
        {
            fixed (byte* pointer = &buffer[offset])
            {
                if (Native.SSL_read_ex(_ssl, (IntPtr)pointer, (nuint)count, out var read) == 1)
                    return checked((int)read);
                if (Native.SSL_get_error(_ssl, 0) == SslErrorZeroReturn) return 0;
                throw CreateException("read failed");
            }
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var written = 0;
        unsafe
        {
            fixed (byte* pointer = &buffer[offset])
            {
                while (written < count)
                {
                    if (Native.SSL_write_ex(_ssl, (IntPtr)(pointer + written), (nuint)(count - written), out var chunk) != 1)
                        throw CreateException("write failed");
                    written += checked((int)chunk);
                }
            }
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); return Read(buffer, offset, count); }, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); Write(buffer, offset, count); }, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(buffer.Length);
        return AwaitReadAsync(rented, buffer, cancellationToken);
    }

    private async ValueTask<int> AwaitReadAsync(byte[] rented, Memory<byte> destination, CancellationToken cancellationToken)
    {
        try
        {
            var read = await ReadAsync(rented, 0, destination.Length, cancellationToken).ConfigureAwait(false);
            rented.AsMemory(0, read).CopyTo(destination);
            return read;
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(rented); }
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var copy = buffer.ToArray();
        return new ValueTask(WriteAsync(copy, 0, copy.Length, cancellationToken));
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        try { Native.SSL_shutdown(_ssl); } catch { }
        Native.SSL_free(_ssl);
        Native.SSL_CTX_free(_context);
        RemoteCertificate?.Dispose();
        if (disposing) _transport.Dispose();
        base.Dispose(disposing);
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private static class Native
    {
        [DllImport(LibSsl, CallingConvention = CallingConvention.Cdecl)] internal static extern int OPENSSL_init_ssl(ulong options, IntPtr settings);
        [DllImport(LibSsl, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr TLS_client_method();
        [DllImport(LibSsl, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SSL_CTX_new(IntPtr method);
        [DllImport(LibSsl, CallingConvention = CallingConvention.Cdecl)] internal static extern void SSL_CTX_free(IntPtr context);
        [DllImport(LibSsl, CallingConvention = CallingConvention.Cdecl)] internal static extern long SSL_CTX_ctrl(IntPtr context, int command, long argument, IntPtr pointer);
        [DllImport(LibSsl, CallingConvention = CallingConvention.Cdecl)] internal static extern int SSL_CTX_set_cipher_list(IntPtr context, [MarshalAs(UnmanagedType.LPStr)] string list);
        [DllImport(LibSsl, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SSL_new(IntPtr context);
        [DllImport(LibSsl, CallingConvention = CallingConvention.Cdecl)] internal static extern void SSL_free(IntPtr ssl);
        [DllImport(LibSsl, CallingConvention = CallingConvention.Cdecl)] internal static extern long SSL_ctrl(IntPtr ssl, int command, long argument, IntPtr pointer);
        [DllImport(LibSsl, CallingConvention = CallingConvention.Cdecl)] internal static extern int SSL_set_fd(IntPtr ssl, int socket);
        [DllImport(LibSsl, CallingConvention = CallingConvention.Cdecl)] internal static extern int SSL_connect(IntPtr ssl);
        [DllImport(LibSsl, CallingConvention = CallingConvention.Cdecl)] internal static extern int SSL_shutdown(IntPtr ssl);
        [DllImport(LibSsl, CallingConvention = CallingConvention.Cdecl)] internal static extern int SSL_read_ex(IntPtr ssl, IntPtr buffer, nuint count, out nuint read);
        [DllImport(LibSsl, CallingConvention = CallingConvention.Cdecl)] internal static extern int SSL_write_ex(IntPtr ssl, IntPtr buffer, nuint count, out nuint written);
        [DllImport(LibSsl, CallingConvention = CallingConvention.Cdecl)] internal static extern int SSL_get_error(IntPtr ssl, int result);
        [DllImport(LibSsl, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SSL_get_version(IntPtr ssl);
        [DllImport(LibSsl, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SSL_get_current_cipher(IntPtr ssl);
        [DllImport(LibSsl, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SSL_CIPHER_get_name(IntPtr cipher);
        [DllImport(LibSsl, CallingConvention = CallingConvention.Cdecl)] internal static extern int SSL_CIPHER_get_bits(IntPtr cipher, out int algorithmBits);
        [DllImport(LibSsl, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr SSL_get1_peer_certificate(IntPtr ssl);
        [DllImport(LibCrypto, CallingConvention = CallingConvention.Cdecl, EntryPoint = "i2d_X509")] internal static extern int i2d_X509_length(IntPtr certificate, IntPtr output);
        [DllImport(LibCrypto, CallingConvention = CallingConvention.Cdecl, EntryPoint = "i2d_X509")] internal static extern int i2d_X509(IntPtr certificate, ref IntPtr output);
        [DllImport(LibCrypto, CallingConvention = CallingConvention.Cdecl)] internal static extern void X509_free(IntPtr certificate);
        [DllImport(LibCrypto, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong ERR_get_error();
        [DllImport(LibCrypto, CallingConvention = CallingConvention.Cdecl)] internal static extern void ERR_error_string_n(ulong error, byte[] buffer, nuint length);
    }
}
