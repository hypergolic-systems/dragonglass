// Reflection shim around `System.Net.WebSockets.ManagedWebSocket.CreateFromConnectedStream`.
//
// Why reflection: `ManagedWebSocket` is `internal sealed` in the Unity/Mono
// build that ships with KSP 1.12.5, but its static factory is a real working
// RFC 6455 implementation. The only public caller (`WebSocket.CreateClientWebSocket`)
// hard-codes `isServer: false`, which is the wrong masking behaviour for
// a server. Reflection lets us reach the same factory with `isServer: true`
// without reimplementing ~300 lines of frame handling, UTF-8 validation,
// fragmentation, close handshake, and keep-alive ping logic.
//
// Safety: KSP 1.12.5 is the final game version — the runtime DLLs are
// frozen forever. The usual "internal APIs might get renamed" concern
// doesn't apply.

using System;
using System.IO;
using System.Net.WebSockets;
using System.Reflection;

namespace Dragonglass.Telemetry.WebSocket
{
    internal static class ManagedWebSocketFactory
    {
        private static readonly MethodInfo _create = ResolveFactory();

        private static MethodInfo ResolveFactory()
        {
            Type t = typeof(System.Net.WebSockets.WebSocket).Assembly
                .GetType("System.Net.WebSockets.ManagedWebSocket", throwOnError: true);
            MethodInfo m = t.GetMethod(
                "CreateFromConnectedStream",
                BindingFlags.Public | BindingFlags.Static);
            if (m == null)
            {
                throw new MissingMethodException(
                    "ManagedWebSocket.CreateFromConnectedStream not found — " +
                    "Unity/Mono build may be older or stripped.");
            }
            return m;
        }

        /// <summary>
        /// Wrap an already-handshaken TCP stream as a server-side WebSocket.
        /// Ownership of the stream transfers to the returned WebSocket; it
        /// will be closed when the WebSocket is disposed.
        /// </summary>
        public static System.Net.WebSockets.WebSocket CreateServerSide(
            Stream stream, TimeSpan keepAliveInterval, int receiveBufferSize)
        {
            return (System.Net.WebSockets.WebSocket)_create.Invoke(null, new object[]
            {
                stream,
                /* isServer: */ true,
                /* subProtocol: */ null,
                keepAliveInterval,
                receiveBufferSize,
                /* receiveBuffer: */ null,
            });
        }
    }
}
