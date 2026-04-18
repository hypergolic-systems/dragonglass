// Per-client state wrapper around a server-side `System.Net.WebSockets.WebSocket`.
// The reader thread loops on ReceiveAsync so the underlying ManagedWebSocket
// can process incoming control frames (close, pong) even when the application
// layer mostly cares about sending. Text frames are assembled across fragment
// boundaries and handed to `onText` (typically the op dispatcher).
//
// Concurrency: SendText may be called from any thread, but at most one
// SendAsync is in flight at a time (protected by `_sendLock`). The reader
// thread is the only one that touches ReceiveAsync — so `onText` is always
// invoked on the reader thread; handlers must be thread-safe relative to
// Unity's main thread (typically by enqueuing onto a main-thread-drained
// queue).

using System;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Dragonglass.Telemetry.WebSocket
{
    public sealed class WebSocketConnection : IDisposable
    {
        private const string LogPrefix = "[Dragonglass/Telemetry] ";

        private readonly System.Net.WebSockets.WebSocket _ws;
        private readonly TcpClient _tcp;
        private readonly Action<WebSocketConnection> _onClosed;
        private readonly Action<WebSocketConnection, string> _onText;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _sendLock = new object();
        private readonly string _remote;
        private Thread _readerThread;
        private volatile bool _disposed;

        public string Remote { get { return _remote; } }

        public WebSocketConnection(
            System.Net.WebSockets.WebSocket ws,
            TcpClient tcp,
            Action<WebSocketConnection> onClosed,
            Action<WebSocketConnection, string> onText = null)
        {
            _ws = ws;
            _tcp = tcp;
            _onClosed = onClosed;
            _onText = onText;
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
            // Accumulator for text frames that span multiple reads. Reset
            // after each EndOfMessage; sized for typical op envelopes
            // (tens of bytes) but grows cheaply for larger payloads.
            MemoryStream textAccum = null;
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

                    if (result.MessageType == WebSocketMessageType.Text &&
                        _onText != null && result.Count > 0)
                    {
                        if (textAccum == null) textAccum = new MemoryStream();
                        textAccum.Write(buf, 0, result.Count);
                        if (result.EndOfMessage)
                        {
                            string text;
                            try
                            {
                                text = Encoding.UTF8.GetString(
                                    textAccum.GetBuffer(), 0, (int)textAccum.Length);
                            }
                            catch (Exception e)
                            {
                                Debug.LogWarning(LogPrefix + "bad UTF-8 from " +
                                                 _remote + ": " + e.Message);
                                textAccum.SetLength(0);
                                continue;
                            }
                            textAccum.SetLength(0);
                            try { _onText(this, text); }
                            catch (Exception e)
                            {
                                Debug.LogWarning(LogPrefix + "onText handler threw: " +
                                                 e.Message);
                            }
                        }
                    }
                    // Binary frames are ignored.
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
