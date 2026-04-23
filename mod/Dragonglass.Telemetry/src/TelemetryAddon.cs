// Dragonglass Telemetry plugin entry point.
//
// Spins up the WebSocket broadcast server at game start, wires up the
// topic registry + broadcaster, and attaches the initial set of Topic
// components (just GameTopic today) to its own GameObject. The
// GameObject survives scene transitions via DontDestroyOnLoad, so the
// server and all topics stay alive across main menu ↔ Flight ↔ VAB.
//
// Third-party mods can reach the host GameObject via
//   GameObject.Find("Dragonglass.Telemetry")
// and `AddComponent<TheirCustomTopic>()` to publish their own topics
// through the same broadcast pipe.

using System;
using System.Net;
using Dragonglass.Telemetry.Topics;
using Dragonglass.Telemetry.WebSocket;
using UnityEngine;

namespace Dragonglass.Telemetry
{
    [KSPAddon(KSPAddon.Startup.Instantly, once: true)]
    public class TelemetryAddon : MonoBehaviour
    {
        private const string LogPrefix = "[Dragonglass/Telemetry] ";
        private const string HostGameObjectName = "Dragonglass.Telemetry";
        private const int Port = 8787;

        private static TelemetryAddon _instance;

        private WebSocketServer _server;
        private TopicRegistry _registry;
        private TopicBroadcaster _broadcaster;
        private OpDispatcher _dispatcher;

        private void Awake()
        {
            // once: true should guarantee a single instance, but belt
            // and braces — we don't want two servers fighting for the port.
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning(LogPrefix + "duplicate addon instance; destroying");
                Destroy(gameObject);
                return;
            }
            _instance = this;
            gameObject.name = HostGameObjectName;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            if (_instance != this) return;

            try
            {
                _server = new WebSocketServer(IPAddress.Loopback, Port);
                _server.Start();
            }
            catch (Exception e)
            {
                Debug.LogError(LogPrefix + "failed to start server: " + e);
                _server = null;
                return;
            }

            _registry = new TopicRegistry();
            TopicRegistry.SetInstance(_registry);
            _broadcaster = new TopicBroadcaster(_registry, _server);
            _dispatcher = new OpDispatcher(_registry, _server);

            // Topics self-register via their OnEnable hook.
            gameObject.AddComponent<GameTopic>();
            gameObject.AddComponent<ClockTopic>();
            // PartSubscriptionManager is scene-agnostic — it listens
            // for subscribe/unsubscribe signals and attaches PartTopic
            // components to Parts themselves. Outside Flight,
            // FlightGlobals.PersistentLoadedPartIds is empty, so its
            // handlers silently drop requests. See
            // Topics/PartSubscriptionManager.cs.
            gameObject.AddComponent<PartSubscriptionManager>();
        }

        private void Update()
        {
            // Drain inbound ops first so any state they change is
            // reflected in the same frame's broadcast.
            _dispatcher?.Drain();
            _broadcaster?.Tick();
        }

        private void OnDestroy()
        {
            if (_instance != this) return;
            if (_server != null)
            {
                _server.Dispose();
                _server = null;
            }
            _instance = null;
            Debug.Log(LogPrefix + "addon destroyed");
        }
    }
}
