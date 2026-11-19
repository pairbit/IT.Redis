using System;
using System.Security.Cryptography;
using System.Text;
using RESPite.Buffers;
using StackExchange.Redis.Protocol;

namespace StackExchange.Redis
{
    internal class CachedLuaScript : IRespPreambleGate
    {
        private readonly string _str;
        private readonly byte[] _bytes;
        private readonly byte[] _hash;
        private readonly int _argCount;

        public string String => _str;
        public byte[] Bytes => _bytes;
        public int Length => _bytes.Length;

        public byte[] Hash => _hash;

        public CachedLuaScript(string script)
        {
            _str = script;
            _bytes = Encoding.UTF8.GetBytes(script);
            _hash = Encoding.ASCII.GetBytes(Sha1Hex(_bytes));
            _argCount = 0; // TODO: calc?
        }

        public bool IsNeeded(IRespPreambleTarget connection)
            => connection.Server is not { } endpoint || !endpoint.IsScriptLoaded(_str);

        public void OnEstablished(IRespPreambleTarget connection)
            => connection.Server?.AddScript(_str, _hash);

        internal RespRequest AsRequest()
            => new(_bytes, RefCountedBuffer.CreateFixed(_bytes), 0, _bytes.Length, argCount: _argCount, command: RedisCommand.SCRIPT);

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
