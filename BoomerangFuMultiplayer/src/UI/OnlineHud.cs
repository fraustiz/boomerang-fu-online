using System.Collections.Generic;
using BoomerangFuMultiplayer.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BoomerangFuMultiplayer.UI
{
    /// <summary>Small always-on overlay: session badge (top right) and toasts (bottom), in the game's font.</summary>
    public sealed class OnlineHud : MonoBehaviour
    {
        TMP_Text _badge, _toast;
        readonly Queue<string> _queue = new Queue<string>();
        float _toastUntil;
        float _lastEscape = -10f;
        bool _built;

        void Start()
        {
            if (Session.Instance != null) Session.Instance.Toast += Show;
        }

        public void Show(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            if (_queue.Count > 4) _queue.Dequeue();
            _queue.Enqueue(message);
        }

        void Build()
        {
            if (OnlineMenu.Font == null) return;
            var canvasGo = new GameObject("BFUMP_Hud");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            _badge = Label(canvasGo.transform, "Badge", new Vector2(1, 1), new Vector2(-130, -30), 26, TextAlignmentOptions.TopRight);
            _toast = Label(canvasGo.transform, "Toast", new Vector2(0.5f, 0), new Vector2(0, 70), 34, TextAlignmentOptions.Bottom);
            _built = true;
        }

        static TMP_Text Label(Transform parent, string name, Vector2 anchor, Vector2 pos, float size, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(1200, 120);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.font = OnlineMenu.Font;
            if (OnlineMenu.FontMaterial != null) t.fontSharedMaterial = OnlineMenu.FontMaterial;
            t.fontSize = size;
            // dark outline: readable on light screens too (character select, snowy levels)
            t.outlineWidth = 0.22f;
            t.outlineColor = new Color32(30, 30, 40, 220);
            t.alignment = align;
            t.enableWordWrapping = false;
            t.raycastTarget = false;
            t.text = "";
            return t;
        }

        void Update()
        {
            if (!_built) { Build(); if (!_built) return; }
            var s = Session.Instance;

            // Client: double Escape leaves the session (Escape alone no longer pauses everyone).
            if (Session.IsClient && Input.GetKeyDown(KeyCode.Escape))
            {
                if (Time.unscaledTime - _lastEscape < 2f) { s.Leave(); _lastEscape = -10f; }
                else { _lastEscape = Time.unscaledTime; Show("Appuie encore sur Échap pour quitter la session"); }
            }

            if (Session.Active)
            {
                string role = s.Role == Role.Host ? "HÔTE" : "JOUEUR " + (s.LocalSlot + 1);
                int ping = s.PingToHost;
                _badge.text = $"<color=#7CFF6B>●</color> EN LIGNE  {s.PlayerCount}/{Protocol.MaxSlots}  ·  {role}" + (ping >= 0 ? $"  ·  {ping} ms" : "");
            }
            else if (s != null && s.Connecting) _badge.text = "<color=#FFD25A>●</color> CONNEXION…";
            else _badge.text = "";

            if (Time.unscaledTime > _toastUntil)
            {
                if (_queue.Count > 0) { _toast.text = _queue.Dequeue(); _toastUntil = Time.unscaledTime + 3f; _toast.alpha = 1f; }
                else _toast.text = "";
            }
            else
            {
                float left = _toastUntil - Time.unscaledTime;
                _toast.alpha = Mathf.Clamp01(left / 0.4f);
            }
        }
    }
}
