using StreamExtended.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace StreamExtended
{
    /// <summary>
    /// Wraps up the client SSL hello information.
    /// </summary>
    public class ClientHelloInfo
    {
        private static readonly string[] compressions = {
            "null",
            "DEFLATE"
        };

        public int HandshakeVersion { get; set; }

        public int MajorVersion { get; set; }

        public int MinorVersion { get; set; }

        public byte[] Random { get; set; }

        public DateTime Time
        {
            get
            {
                DateTime time = DateTime.MinValue;
                if (Random.Length > 3)
                {
                    time = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)
                        .AddSeconds(((uint)Random[3] << 24) + ((uint)Random[2] << 16) + ((uint)Random[1] << 8) + (uint)Random[0]).ToLocalTime();
                }

                return time;
            }
        }

        public byte[] SessionId { get; set; }

        public int[] Ciphers { get; set; }

        public byte[] CompressionData { get; set; }

        internal int ClientHelloLength { get; set; }

        internal int EntensionsStartPosition { get; set; }

        public Dictionary<string, SslExtension> Extensions { get; set; }

        private static string SslVersionToString(int major, int minor)
        {
            string str = "Unknown";
            if (major == 3 && minor == 3)
                str = "TLS/1.2";
            else if (major == 3 && minor == 2)
                str = "TLS/1.1";
            else if (major == 3 && minor == 1)
                str = "TLS/1.0";
            else if (major == 3 && minor == 0)
                str = "SSL/3.0";
            else if (major == 2 && minor == 0)
                str = "SSL/2.0";

            return $"{major}.{minor} ({str})";
        }

        /// <summary>
        /// Returns a <see cref="System.String" /> that represents this instance.
        /// </summary>
        /// <returns>
        /// A <see cref="System.String" /> that represents this instance.
        /// </returns>
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"A SSLv{HandshakeVersion}-compatible ClientHello handshake was found. Titanium extracted the parameters below.");
            sb.AppendLine();
            sb.AppendLine($"Version: {SslVersionToString(MajorVersion, MinorVersion)}");
            sb.AppendLine($"Random: {string.Join(" ", Random.Select(x => x.ToString("X2")))}");
            sb.AppendLine($"\"Time\": {Time}");
            sb.AppendLine($"SessionID: {string.Join(" ", SessionId.Select(x => x.ToString("X2")))}");

            if (Extensions != null)
            {
                sb.AppendLine("Extensions:");
                foreach (var extension in Extensions.Values.OrderBy(x => x.Position))
                {
                    sb.AppendLine($"{extension.Name}: {extension.Data}");
                }
            }

            if (CompressionData != null && CompressionData.Length > 0)
            {
                int compressionMethod = CompressionData[0];
                string compression = compressions.Length > compressionMethod 
                    ? compressions[compressionMethod] 
                    : $"unknown [0x{compressionMethod:X2}]";
                sb.AppendLine($"Compression: {compression}");
            }

            if (Ciphers.Length > 0)
            {
                sb.AppendLine("Ciphers:");
                foreach (int cipherSuite in Ciphers)
                {
                    if (!SslCiphers.Ciphers.TryGetValue(cipherSuite, out string cipherStr))
                    {
                        cipherStr = "unknown";
                    }

                    sb.AppendLine($"[0x{cipherSuite:X4}] {cipherStr}");
                }
            }

            return sb.ToString();
        }
    }
}