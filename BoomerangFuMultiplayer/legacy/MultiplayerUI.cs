using UnityEngine;

namespace BoomerangFuMultiplayer
{
    // Minimal always-on IMGUI overlay.
    // Toggle with the small [M] button in the top-left corner.
    internal sealed class MultiplayerUI : MonoBehaviour
    {
        private string _ip;
        private string _status  = "Offline";
        private bool   _visible = true;

        void Awake()
        {
            DontDestroyOnLoad(gameObject);
            _ip = Plugin.CfgLastIP.Value;
        }

        void Start()
        {
            var net = NetLayer.Instance;
            if (net == null) return;

            net.OnClientJoined += slot => _status = $"Client {slot} joined";
            net.OnClientLeft   += slot => _status = $"Client {slot} left";
            net.OnJoinedHost   += ()   => _status = $"Connected — slot {net.LocalSlot}";
            net.OnLeftHost     += ()   => _status = "Disconnected";
        }

        void OnGUI()
        {
            // Toggle button always visible in corner
            if (GUI.Button(new Rect(4, 4, 24, 22), "M"))
                _visible = !_visible;

            if (!_visible) return;

            GUI.Box(new Rect(4, 30, 215, 165), "  Boomerang Fu — Multiplayer");
            GUILayout.BeginArea(new Rect(12, 54, 202, 138));

            var net = NetLayer.Instance;

            GUILayout.Label($"Status: {_status}");

            if (net != null && !net.IsConnected)
            {
                // ── Not connected ──────────────────────────────────────────────
                GUILayout.Label("Host IP (Hamachi or LAN):");
                _ip = GUILayout.TextField(_ip ?? "", 24);

                GUILayout.BeginHorizontal();

                if (GUILayout.Button("Host"))
                {
                    Plugin.CfgLastIP.Value = _ip;
                    net.StartHost(Plugin.CfgPort.Value);
                    GameSync.Instance?.StartBroadcasting();
                    _status = $"Hosting :{Plugin.CfgPort.Value}  — waiting…";
                }

                if (GUILayout.Button("Join"))
                {
                    Plugin.CfgLastIP.Value = _ip;
                    net.StartClient(_ip, Plugin.CfgPort.Value);
                    _status = "Connecting…";
                }

                GUILayout.EndHorizontal();

                GUILayout.Label("(port: " + Plugin.CfgPort.Value + ")");
            }
            else if (net != null)
            {
                // ── Connected ──────────────────────────────────────────────────
                if (net.IsHost)
                    GUILayout.Label("Role: HOST  (authoritative sim)");
                else
                    GUILayout.Label($"Role: CLIENT  ping:{net.Latency} ms");

                if (GUILayout.Button("Disconnect"))
                {
                    net.Shutdown();
                    _status = "Offline";
                }
            }

            GUILayout.EndArea();
        }
    }
}
