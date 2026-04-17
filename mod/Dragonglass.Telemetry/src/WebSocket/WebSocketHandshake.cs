// RFC 6455 opening handshake. Reads the HTTP GET request off a fresh
// TCP stream, validates Upgrade + Sec-WebSocket-Version + Sec-WebSocket-Key,
// computes the required Sec-WebSocket-Accept value, and writes the
// 101 Switching Protocols response. On success the stream is left
// positioned at the start of the WebSocket frame byte stream — hand it
// straight to `ManagedWebSocketFactory.CreateServerSide`.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Dragonglass.Telemetry.WebSocket
{
    internal static class WebSocketHandshake
    {
        // RFC 6455 §1.3 — concatenated with Sec-WebSocket-Key before SHA-1.
        private const string Magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        // Cap the header blob so a malicious client can't blow up memory.
        private const int MaxRequestBytes = 8 * 1024;

        /// <summary>
        /// Perform the handshake. Returns true on success; false on any
        /// protocol violation (also writes a 400 response in that case).
        /// </summary>
        public static bool TryAccept(Stream stream)
        {
            byte[] requestBytes;
            if (!TryReadRequestHead(stream, out requestBytes))
            {
                WriteBadRequest(stream, "request too long or connection closed");
                return false;
            }

            string request = Encoding.ASCII.GetString(requestBytes);
            Dictionary<string, string> headers;
            string requestLine;
            if (!TryParse(request, out requestLine, out headers))
            {
                WriteBadRequest(stream, "malformed HTTP request");
                return false;
            }

            // Method + protocol sanity.
            if (!requestLine.StartsWith("GET ", StringComparison.Ordinal) ||
                !requestLine.EndsWith(" HTTP/1.1", StringComparison.Ordinal))
            {
                WriteBadRequest(stream, "expected GET / HTTP/1.1");
                return false;
            }

            // Upgrade header (case-insensitive).
            string upgrade;
            if (!headers.TryGetValue("upgrade", out upgrade) ||
                !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase))
            {
                WriteBadRequest(stream, "Upgrade: websocket required");
                return false;
            }

            string connection;
            if (!headers.TryGetValue("connection", out connection) ||
                connection.IndexOf("upgrade", StringComparison.OrdinalIgnoreCase) < 0)
            {
                WriteBadRequest(stream, "Connection: Upgrade required");
                return false;
            }

            string version;
            if (!headers.TryGetValue("sec-websocket-version", out version) || version.Trim() != "13")
            {
                WriteBadRequest(stream, "only Sec-WebSocket-Version: 13 is supported");
                return false;
            }

            string key;
            if (!headers.TryGetValue("sec-websocket-key", out key))
            {
                WriteBadRequest(stream, "Sec-WebSocket-Key missing");
                return false;
            }

            string accept = ComputeAccept(key.Trim());

            StringBuilder response = new StringBuilder();
            response.Append("HTTP/1.1 101 Switching Protocols\r\n");
            response.Append("Upgrade: websocket\r\n");
            response.Append("Connection: Upgrade\r\n");
            response.Append("Sec-WebSocket-Accept: ").Append(accept).Append("\r\n");
            response.Append("\r\n");

            byte[] bytes = Encoding.ASCII.GetBytes(response.ToString());
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
            return true;
        }

        private static bool TryReadRequestHead(Stream stream, out byte[] bytes)
        {
            byte[] buf = new byte[MaxRequestBytes];
            int read = 0;
            while (read < buf.Length)
            {
                int n = stream.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
                // Look for CRLF CRLF terminator.
                if (read >= 4 &&
                    buf[read - 4] == '\r' && buf[read - 3] == '\n' &&
                    buf[read - 2] == '\r' && buf[read - 1] == '\n')
                {
                    bytes = new byte[read];
                    Array.Copy(buf, bytes, read);
                    return true;
                }
            }
            bytes = null;
            return false;
        }

        private static bool TryParse(string request, out string requestLine,
                                     out Dictionary<string, string> headers)
        {
            requestLine = null;
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string[] lines = request.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return false;

            requestLine = lines[0];
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) break;  // end of headers
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                headers[name] = value;
            }
            return true;
        }

        private static string ComputeAccept(string key)
        {
            using (SHA1 sha1 = SHA1.Create())
            {
                byte[] bytes = Encoding.ASCII.GetBytes(key + Magic);
                byte[] hash = sha1.ComputeHash(bytes);
                return Convert.ToBase64String(hash);
            }
        }

        private static void WriteBadRequest(Stream stream, string reason)
        {
            try
            {
                string body = "400 Bad Request: " + reason + "\r\n";
                byte[] bodyBytes = Encoding.ASCII.GetBytes(body);
                StringBuilder sb = new StringBuilder();
                sb.Append("HTTP/1.1 400 Bad Request\r\n");
                sb.Append("Content-Type: text/plain; charset=us-ascii\r\n");
                sb.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
                sb.Append("Connection: close\r\n");
                sb.Append("\r\n");
                byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
                stream.Write(headerBytes, 0, headerBytes.Length);
                stream.Write(bodyBytes, 0, bodyBytes.Length);
                stream.Flush();
            }
            catch
            {
                // Best effort. If the client is already gone, swallow.
            }
        }
    }
}
