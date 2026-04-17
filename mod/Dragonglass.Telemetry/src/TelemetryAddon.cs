// Dragonglass Telemetry plugin entry point.
//
// Spins up a WebSocket broadcast server on 127.0.0.1:8787 at game start
// and keeps it alive across scene transitions (main menu ↔ Flight ↔ VAB
// ↔ …) via DontDestroyOnLoad. For the MVP, we broadcast a `{"tick":N}`
// heartbeat at 10 Hz — that's enough to prove the pipe end-to-end. Real
// vessel state (altitude, orientation, stage data, …) comes next.

using System;
using System.Net;
using Dragonglass.Telemetry.WebSocket;
using UnityEngine;

namespace Dragonglass.Telemetry
{
    [KSPAddon(KSPAddon.Startup.Instantly, once: true)]
    public class TelemetryAddon : MonoBehaviour
    {
        private const string LogPrefix = "[Dragonglass/Telemetry] ";
        private const int Port = 8787;
        private const float BroadcastIntervalSec = 0.1f;

        private static TelemetryAddon _instance;

        private WebSocketServer _server;
        private float _nextBroadcast;
        private long _tick;

        private void Awake()
        {
            // Belt-and-braces: `once: true` should already prevent this,
            // but if KSP re-registers the addon for any reason, we don't
            // want two instances fighting for the port.
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning(LogPrefix + "duplicate addon instance; destroying");
                Destroy(gameObject);
                return;
            }
            _instance = this;
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
            }
        }

        private void Update()
        {
            if (_server == null) return;
            if (Time.realtimeSinceStartup < _nextBroadcast) return;
            _nextBroadcast = Time.realtimeSinceStartup + BroadcastIntervalSec;
            _server.Broadcast("{\"tick\":" + (++_tick) + "}");
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
