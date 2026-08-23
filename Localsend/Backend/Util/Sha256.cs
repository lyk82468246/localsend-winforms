using System;
using System.Text;

namespace Localsend.Backend.Util
{
    /// <summary>
    /// Small SHA-256 implementation used for LocalSend certificate
    /// fingerprints.  Compact Framework 3.5 exposes SHA-1 but not the SHA-2
    /// managed classes, so this keeps the fingerprint format identical on
    /// CE and desktop without loading another crypto assembly.
    /// </summary>
    internal static class Sha256
    {
        private static readonly uint[] K = new uint[]
        {
            0x428a2f98U, 0x71374491U, 0xb5c0fbcfU, 0xe9b5dba5U,
            0x3956c25bU, 0x59f111f1U, 0x923f82a4U, 0xab1c5ed5U,
            0xd807aa98U, 0x12835b01U, 0x243185beU, 0x550c7dc3U,
            0x72be5d74U, 0x80deb1feU, 0x9bdc06a7U, 0xc19bf174U,
            0xe49b69c1U, 0xefbe4786U, 0x0fc19dc6U, 0x240ca1ccU,
            0x2de92c6fU, 0x4a7484aaU, 0x5cb0a9dcU, 0x76f988daU,
            0x983e5152U, 0xa831c66dU, 0xb00327c8U, 0xbf597fc7U,
            0xc6e00bf3U, 0xd5a79147U, 0x06ca6351U, 0x14292967U,
            0x27b70a85U, 0x2e1b2138U, 0x4d2c6dfcU, 0x53380d13U,
            0x650a7354U, 0x766a0abbU, 0x81c2c92eU, 0x92722c85U,
            0xa2bfe8a1U, 0xa81a664bU, 0xc24b8b70U, 0xc76c51a3U,
            0xd192e819U, 0xd6990624U, 0xf40e3585U, 0x106aa070U,
            0x19a4c116U, 0x1e376c08U, 0x2748774cU, 0x34b0bcb5U,
            0x391c0cb3U, 0x4ed8aa4aU, 0x5b9cca4fU, 0x682e6ff3U,
            0x748f82eeU, 0x78a5636fU, 0x84c87814U, 0x8cc70208U,
            0x90befffaU, 0xa4506cebU, 0xbef9a3f7U, 0xc67178f2U
        };

        public static byte[] Compute(byte[] input)
        {
            if (input == null) throw new ArgumentNullException("input");

            int total = input.Length + 9;
            total = ((total + 63) / 64) * 64;
            byte[] padded = new byte[total];
            Buffer.BlockCopy(input, 0, padded, 0, input.Length);
            padded[input.Length] = 0x80;

            ulong bitLength = (ulong)input.Length * 8UL;
            for (int i = 0; i < 8; i++)
                padded[total - 1 - i] = (byte)(bitLength >> (8 * i));

            uint h0 = 0x6a09e667U;
            uint h1 = 0xbb67ae85U;
            uint h2 = 0x3c6ef372U;
            uint h3 = 0xa54ff53aU;
            uint h4 = 0x510e527fU;
            uint h5 = 0x9b05688cU;
            uint h6 = 0x1f83d9abU;
            uint h7 = 0x5be0cd19U;
            uint[] w = new uint[64];

            for (int offset = 0; offset < padded.Length; offset += 64)
            {
                for (int i = 0; i < 16; i++)
                {
                    int p = offset + (i * 4);
                    w[i] = ((uint)padded[p] << 24)
                         | ((uint)padded[p + 1] << 16)
                         | ((uint)padded[p + 2] << 8)
                         | padded[p + 3];
                }
                for (int i = 16; i < 64; i++)
                {
                    uint s0 = Ror(w[i - 15], 7) ^ Ror(w[i - 15], 18) ^ (w[i - 15] >> 3);
                    uint s1 = Ror(w[i - 2], 17) ^ Ror(w[i - 2], 19) ^ (w[i - 2] >> 10);
                    w[i] = w[i - 16] + s0 + w[i - 7] + s1;
                }

                uint a = h0, b = h1, c = h2, d = h3;
                uint e = h4, f = h5, g = h6, h = h7;
                for (int i = 0; i < 64; i++)
                {
                    uint s1 = Ror(e, 6) ^ Ror(e, 11) ^ Ror(e, 25);
                    uint ch = (e & f) ^ (~e & g);
                    uint temp1 = h + s1 + ch + K[i] + w[i];
                    uint s0 = Ror(a, 2) ^ Ror(a, 13) ^ Ror(a, 22);
                    uint maj = (a & b) ^ (a & c) ^ (b & c);
                    uint temp2 = s0 + maj;
                    h = g;
                    g = f;
                    f = e;
                    e = d + temp1;
                    d = c;
                    c = b;
                    b = a;
                    a = temp1 + temp2;
                }

                h0 += a; h1 += b; h2 += c; h3 += d;
                h4 += e; h5 += f; h6 += g; h7 += h;
            }

            byte[] result = new byte[32];
            WriteUInt(result, 0, h0); WriteUInt(result, 4, h1);
            WriteUInt(result, 8, h2); WriteUInt(result, 12, h3);
            WriteUInt(result, 16, h4); WriteUInt(result, 20, h5);
            WriteUInt(result, 24, h6); WriteUInt(result, 28, h7);
            return result;
        }

        public static string ToUpperHex(byte[] bytes)
        {
            if (bytes == null) return "";
            const string hex = "0123456789ABCDEF";
            char[] chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = hex[bytes[i] >> 4];
                chars[i * 2 + 1] = hex[bytes[i] & 0x0f];
            }
            return new string(chars);
        }

        private static uint Ror(uint value, int bits)
        { return (value >> bits) | (value << (32 - bits)); }

        private static void WriteUInt(byte[] output, int offset, uint value)
        {
            output[offset] = (byte)(value >> 24);
            output[offset + 1] = (byte)(value >> 16);
            output[offset + 2] = (byte)(value >> 8);
            output[offset + 3] = (byte)value;
        }
    }
}
