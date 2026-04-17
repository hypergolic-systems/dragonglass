// Per-client state wrapper around a server-side `System.Net.WebSockets.WebSocket`.
// The reader thread loops on ReceiveAsync so the underlying ManagedWebSocket
// can process incoming control frames (close, pong) even when the application
// layer only cares about sending. On close or error the connection removes
// itself from the server's client list via the provided callback.
//
// Concurrency: SendText may be called from any thread, but at most one
// SendAsync is in flight at a time (protected by `_sendLock`). The reader
// thread is the only one that touches ReceiveAsync.

using System;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Dragonglass.Telemetry.WebSocket
{
    internal sealed class WebSocketConnection : IDisposable
    {
        private const string LogPrefix = "[Dragonglass/Telemetry] ";

        private readonly System.Net.WebSockets.WebSocket _ws;
        private readonly TcpClient _tcp;
        private readonly Action<WebSocketConnection> _onClosed;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _sendLock = new object();
        private readonly string _remote;
        private Thread _readerThread;
        private volatile bool _disposed;

        public string Remote { get { return _remote; } }

        public WebSocketConnection(
            System.Net.WebSockets.WebSocket ws,
            TcpClient tcp,
            Action<WebSocketConnection> onClosed)
        {
            _ws = ws;
            _tcp = tcp;
            _onClosed = onClosed;
            _remote = tcp.Client.RemoteEndPoint != null
                ? tcp.Client.RemoteEndPoint.ToString()
                : "<unknown>";
        }

        public void StartReader()
        {
            _readerThread = new Thread(ReaderLoop)
            {
                IsBackground = true,
                Name = "Dragonglass.Telemetry.Reader",
            };
            _readerThread.Start();
        }

        /// <summary>
        /// Encode <paramref name="text"/> as a UTF-8 text frame and write
        /// it to the client. Returns false on any send failure (client
        /// will be closed + removed).
        /// </summary>
        public bool SendText(string text)
        {
            if (_disposed || _ws.State != WebSocketState.Open) return false;
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            var segment = new ArraySegment<byte>(bytes);
            try
            {
                lock (_sendLock)
                {
                    // Synchronous wait — the caller (Unity main thread)
                    // provides back-pressure for us.
                    _ws.SendAsync(segment, WebSocketMessageType.Text,
                        endOfMessage: true, _cts.Token).Wait();
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning(LogPrefix + "send to " + _remote + " failed: " + e.Message);
                Dispose();
                return false;
            }
        }

        private void ReaderLoop()
        {
            byte[] buf = new byte[4096];
            var segment = new ArraySegment<byte>(buf);
            try
            {
                while (!_disposed && _ws.State == WebSocketState.Open)
                {
                    Task<WebSocketReceiveResult> t;
                    try
                    {
                        t = _ws.ReceiveAsync(segment, _cts.Token);
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (OperationCanceledException) { break; }

                    WebSocketReceiveResult result;
                    try
                    {
                        result = t.Result;
                    }
                    catch (AggregateException ae)
                        when (ae.InnerException is OperationCanceledException) { break; }
                    catch (AggregateException ae)
                        when (ae.InnerException is WebSocketException) { break; }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // ManagedWebSocket automatically echoes the close
                        // frame; we just exit the loop.
                        break;
                    }
                    // Text/binary inbound — MVP is one-way broadcast, discard.
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(LogPrefix + "reader for " + _remote + " threw: " + e.Message);
            }
            finally
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _cts.Cancel(); } catch { /* ignore */ }
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    // Best-effort close; don't wait — the peer may be gone.
                    _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,
                        "server shutting down", CancellationToken.None);
                }
            }
            catch { /* ignore */ }
            try { _ws.Dispose(); } catch { /* ignore */ }
            try { _tcp.Close(); } catch { /* ignore */ }
            try { _cts.Dispose(); } catch { /* ignore */ }

            if (_onClosed != null)
            {
                try { _onClosed(this); } catch { /* ignore */ }
            }
        }
    }
}
