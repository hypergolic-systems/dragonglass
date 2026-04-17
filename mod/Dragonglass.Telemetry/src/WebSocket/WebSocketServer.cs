// Multi-client WebSocket broadcast server. Binds a TCP listener, runs an
// accept loop on its own thread, and exposes `Broadcast(string)` for the
// Unity main thread to fan text frames out to every connected client.
//
// The per-connection reader threads are spawned inside `WebSocketConnection`
// — they keep the underlying ManagedWebSocket alive for control-frame
// processing (close, pong) even though the application layer is one-way
// for now.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace Dragonglass.Telemetry.WebSocket
{
    public sealed class WebSocketServer : IDisposable
    {
        private const string LogPrefix = "[Dragonglass/Telemetry] ";

        // Keep-alive pings from ManagedWebSocket. Browsers already handle
        // this on their side; 30s is a low-overhead default that still
        // detects half-open connections quickly enough.
        private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

        private const int ReceiveBufferSize = 4096;

        private readonly IPAddress _address;
        private readonly int _port;
        private readonly object _clientLock = new object();
        private readonly List<WebSocketConnection> _clients = new List<WebSocketConnection>();

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _stopping;

        public event Action<int> ClientsChanged;

        /// <summary>
        /// Fires after a client's handshake succeeds and its reader
        /// thread is running. Handlers run on the accept thread — keep
        /// them fast and thread-safe. Typical use: send an initial
        /// snapshot frame to the newly-connected client.
        /// </summary>
        public event Action<WebSocketConnection> ClientConnected;

        public int ClientCount
        {
            get { lock (_clientLock) return _clients.Count; }
        }

        public WebSocketServer() : this(IPAddress.Loopback, 8787) { }

        public WebSocketServer(IPAddress address, int port)
        {
            _address = address;
            _port = port;
        }

        public void Start()
        {
            if (_listener != null)
                throw new InvalidOperationException("already started");

            _listener = new TcpListener(_address, _port);
            _listener.Start();
            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "Dragonglass.Telemetry.Accept",
            };
            _acceptThread.Start();
            Debug.Log(LogPrefix + "server listening on " + _address + ":" + _port);
        }

        /// <summary>
        /// Fan <paramref name="utf8Text"/> out to every connected client.
        /// Failed sends close the offending client; other clients keep
        /// receiving. Safe to call from any thread.
        /// </summary>
        public void Broadcast(string utf8Text)
        {
            if (string.IsNullOrEmpty(utf8Text)) return;

            // Snapshot under lock so we don't hold it while doing network I/O.
            WebSocketConnection[] snapshot;
            lock (_clientLock)
            {
                if (_clients.Count == 0) return;
                snapshot = _clients.ToArray();
            }
            for (int i = 0; i < snapshot.Length; i++)
            {
                snapshot[i].SendText(utf8Text);
            }
        }

        private void AcceptLoop()
        {
            while (!_stopping)
            {
                TcpClient tcp;
                try
                {
                    tcp = _listener.AcceptTcpClient();
                }
                catch (SocketException) when (_stopping) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception e)
                {
                    Debug.LogWarning(LogPrefix + "accept failed: " + e.Message);
                    continue;
                }
                HandleAccepted(tcp);
            }
        }

        private void HandleAccepted(TcpClient tcp)
        {
            NetworkStream stream;
            try
            {
                stream = tcp.GetStream();
            }
            catch (Exception e)
            {
                Debug.LogWarning(LogPrefix + "GetStream failed: " + e.Message);
                try { tcp.Close(); } catch { }
                return;
            }

            bool handshakeOk;
            try
            {
                handshakeOk = WebSocketHandshake.TryAccept(stream);
            }
            catch (Exception e)
            {
                Debug.LogWarning(LogPrefix + "handshake threw: " + e.Message);
                try { tcp.Close(); } catch { }
                return;
            }
            if (!handshakeOk)
            {
                try { tcp.Close(); } catch { }
                return;
            }

            System.Net.WebSockets.WebSocket ws;
            try
            {
                ws = ManagedWebSocketFactory.CreateServerSide(
                    stream, KeepAliveInterval, ReceiveBufferSize);
            }
            catch (Exception e)
            {
                Debug.LogError(LogPrefix + "failed to wrap stream as WebSocket: " + e);
                try { tcp.Close(); } catch { }
                return;
            }

            WebSocketConnection conn = new WebSocketConnection(ws, tcp, RemoveClient);
            lock (_clientLock)
            {
                _clients.Add(conn);
            }
            conn.StartReader();

            int count;
            lock (_clientLock) count = _clients.Count;
            Debug.Log(LogPrefix + "client connected from " + conn.Remote +
                      " (total: " + count + ")");
            RaiseClientConnected(conn);
            RaiseClientsChanged(count);
        }

        private void RaiseClientConnected(WebSocketConnection conn)
        {
            Action<WebSocketConnection> handler = ClientConnected;
            if (handler == null) return;
            try { handler(conn); }
            catch (Exception e) { Debug.LogWarning(LogPrefix + "ClientConnected handler threw: " + e.Message); }
        }

        private void RemoveClient(WebSocketConnection conn)
        {
            int count;
            bool removed;
            lock (_clientLock)
            {
                removed = _clients.Remove(conn);
                count = _clients.Count;
            }
            if (removed)
            {
                Debug.Log(LogPrefix + "client " + conn.Remote + " closed (total: " + count + ")");
                RaiseClientsChanged(count);
            }
        }

        private void RaiseClientsChanged(int count)
        {
            Action<int> handler = ClientsChanged;
            if (handler == null) return;
            try { handler(count); }
            catch (Exception e) { Debug.LogWarning(LogPrefix + "ClientsChanged handler threw: " + e.Message); }
        }

        public void Dispose()
        {
            _stopping = true;
            try { _listener?.Stop(); } catch { }

            WebSocketConnection[] snapshot;
            lock (_clientLock)
            {
                snapshot = _clients.ToArray();
                _clients.Clear();
            }
            for (int i = 0; i < snapshot.Length; i++)
            {
                try { snapshot[i].Dispose(); } catch { }
            }

            // Don't Join() the accept thread — under Mono it can deadlock on
            // Unity shutdown. IsBackground = true is enough; process exit kills it.
        }
    }
}
