using System;
using System.Buffers.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using RESPite.Buffers;
using StackExchange.Redis.Protocol;

namespace StackExchange.Redis
{
    /// <summary>
    /// CachedLuaScript.
    /// </summary>
    public class CachedLuaScript : IRespPreambleGate
    {
        private const int LoadArgCount = 3;
        private readonly byte[] _request;
        private readonly byte[] _hash;
        private readonly string _str;
        private readonly int _start;

        /// <summary>
        /// String.
        /// </summary>
        public string String => _str;

        /// <summary>
        /// Bytes.
        /// </summary>
        public ReadOnlyMemory<byte> Bytes => _request.AsMemory(_start);

        /// <summary>
        /// Hash.
        /// </summary>
        public byte[] Hash => _hash;

        /// <summary>
        /// Initializes a new instance of the <see cref="CachedLuaScript"/> class.
        /// </summary>
        /// <param name="bytes">bytes.</param>
        public CachedLuaScript(byte[] bytes)
        {
            _str = Encoding.UTF8.GetString(bytes);
            _hash = Encoding.ASCII.GetBytes(Sha1Hex(bytes));
            ReadOnlySpan<byte> header = "*3\r\n$6\r\nSCRIPT\r\n$4\r\nLOAD\r\n"u8;
            Span<byte> utf8len = stackalloc byte[10];
#if NET
            var status = bytes.Length.TryFormat(utf8len, out var written);
#else
            var status = Utf8Formatter.TryFormat(bytes.Length, utf8len, out var written);
#endif
            Debug.Assert(status);
            var request = new byte[header.Length + written + 2 + bytes.Length];
            header.CopyTo(request);

            var start = header.Length;
            utf8len.CopyTo(request.AsSpan(start));
            start += written;

            "\r\n"u8.CopyTo(request.AsSpan(start));
            start += 2;

            bytes.CopyTo(request.AsSpan(start));

            _request = request;
            _start = start;
        }

        bool IRespPreambleGate.IsNeeded(IRespPreambleTarget connection)
            => connection.Server is not { } endpoint || !endpoint.IsScriptLoaded(_str);

        void IRespPreambleGate.OnEstablished(IRespPreambleTarget connection)
            => connection.Server?.AddScript(_str, _hash);

        internal RespRequest AsLoadRequest()
            => new(_request, RefCountedBuffer.CreateFixed(_request), 0, _request.Length, argCount: LoadArgCount, command: RedisCommand.SCRIPT);

        private static string Sha1Hex(byte[] bytes)
        {
#if NET
            Span<byte> digest = stackalloc byte[20];
            SHA1.HashData(bytes, digest);
#else
            using var sha = SHA1.Create();
            var digest = sha.ComputeHash(bytes);
#endif
            // one path for every target: lower-case hex is what the server reports, and `ToHexStringLower`
            // is too new to use here. Not hot - and once the rendered SCRIPT LOAD frame is cached, this
            // runs once per script rather than once per call.
            const string Hex = "0123456789abcdef";
            var chars = new char[40];
            for (var i = 0; i < 20; i++)
            {
                chars[i * 2] = Hex[digest[i] >> 4];
                chars[(i * 2) + 1] = Hex[digest[i] & 0xF];
            }

            return new string(chars);
        }
    }
}
