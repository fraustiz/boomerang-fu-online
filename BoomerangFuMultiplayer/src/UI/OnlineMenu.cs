using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using BoomerangFuMultiplayer.Net;
using BoomerangFuMultiplayer.Sync;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BoomerangFuMultiplayer.UI
{
    /// <summary>
    /// The "EN LIGNE" screen. Built at runtime by cloning the game's own pause menu so it looks
    /// and navigates exactly like the other menus (same buttons, fonts, sounds, controller
    /// navigation, back button).
    /// </summary>
    public sealed class OnlineMenu : UIMenuBase
    {
        public const UIMenuBase.MenuType MenuId = (UIMenuBase.MenuType)42;

        public static OnlineMenu Instance { get; private set; }
        public static TMP_FontAsset Font { get; private set; }
        public static Material FontMaterial { get; private set; }

        public static bool IsCurrent
        {
            get
            {
                if (Instance == null) return false;
                var ui = Game.Get<UIManager>();
                return ui != null && ui.GetCurrentMenu() == Instance;
            }
        }

        public static bool Owns(Component c) => c != null && Owns(c.gameObject);
        public static bool Owns(GameObject go) =>
            go != null && ((Instance != null && go.transform.IsChildOf(Instance.transform)) || go.name == MainButtonName);

        const string MainButtonName = "BFUMP_OnlineButton";

        readonly List<UIButton> _pool = new List<UIButton>();
        readonly List<Action> _actions = new List<Action>();
        TMP_Text _title, _status, _players;
        CanvasGroup _group;
        bool _dirty = true;
        float _nextFriendScan;
        List<(string name, CSteamID host)> _friends = new List<(string, CSteamID)>();

        // ── Construction ─────────────────────────────────────────────────────────

        public static void Build(UIManager ui)
        {
            if (Instance != null) return;
            try
            {
                var main = ui.GetMenu(UIMenuBase.MenuType.Main) as UIMenuMain;
                var pause = ui.GetMenu(UIMenuBase.MenuType.Pause);
                if (main == null || pause == null) { Plugin.Log.LogWarning("[OnlineMenu] main/pause menu not found"); return; }
                BuildMenu(ui, pause, main);
                BuildMainButton(main);
                Plugin.Log.LogInfo("[OnlineMenu] online menu ready");
            }
            catch (Exception e) { Plugin.Log.LogError("[OnlineMenu] build failed: " + e); }
        }

        static void StripLocalization(GameObject root)
        {
            foreach (var c in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (c == null) continue;
                var n = c.GetType().Name;
                if (n == "Localize" || n == "LocalizationParamsManager" || n == "GameObjectLocalization" || n == "UIImageLocalization")
                    DestroyImmediate(c);
            }
        }

        static void BuildMenu(UIManager ui, UIMenuBase pause, UIMenuMain main)
        {
            var go = Instantiate(pause.gameObject, pause.transform.parent);
            go.name = "BFUMP_OnlineMenu";
            go.SetActive(false);
            var oldMenu = go.GetComponent<UIMenuBase>();
            var prompts = go.GetComponentsInChildren<ButtonPrompt>(true);
            DestroyImmediate(oldMenu);
            StripLocalization(go);

            var menu = go.AddComponent<OnlineMenu>();
            Instance = menu;
            menu.MenuID = MenuId;
            menu.masterButtonPrompts = prompts;
            menu._group = go.GetComponent<CanvasGroup>();

            // Keep the pause menu's frame (background, corners, header, button list) but not its
            // buttons: their labels are hidden until the pause menu's own animation reveals them.
            // Buttons are cloned from the main menu's "Play" button instead, which always renders.
            var pauseButtons = go.GetComponentsInChildren<UIButton>(true);
            if (pauseButtons.Length == 0) throw new Exception("no button in pause menu");
            var list = pauseButtons[0].transform.parent;
            foreach (var b in pauseButtons) DestroyImmediate(b.gameObject);

            var vlg = list.GetComponent<VerticalLayoutGroup>();
            if (vlg != null) vlg.childAlignment = TextAnchor.UpperCenter;
            // The pause menu's button list has a coloured vertical strip as background (the colour of the
            // player who paused): on this screen it showed as a red bar across the texts.
            var strip = list.GetComponent<Image>();
            if (strip != null) strip.enabled = false;

            // Backdrop: the main menu's illustration (a render texture filled by UIManager.mainMenuBGCamera),
            // under the pause menu's dark veil. The pause menu expects the match behind it; here there is
            // only the empty 3D scene: a bare sky and, with no level loaded, the arena's red boundary glow
            // as a bar in the middle of the screen.
            var art = main.GetComponentsInChildren<RawImage>(true).FirstOrDefault(r => r.texture is RenderTexture);
            if (art != null)
            {
                var artGo = Instantiate(art.gameObject, go.transform);
                artGo.name = "BFUMP_Backdrop";
                artGo.transform.SetSiblingIndex(0);
                for (int i = artGo.transform.childCount - 1; i >= 0; i--) DestroyImmediate(artGo.transform.GetChild(i).gameObject);
                artGo.GetComponent<RawImage>().raycastTarget = false;
                var veil = go.transform.Find("Background")?.GetComponent<Image>();
                if (veil != null) { var c = veil.color; c.a = Mathf.Max(c.a, BackdropVeil); veil.color = c; }
            }
            else Plugin.Log.LogWarning("[OnlineMenu] main menu illustration not found");

            var src = main.initialSelection != null ? main.initialSelection : main.GetComponentInChildren<UIButton>(true)?.gameObject;
            if (src == null) throw new Exception("no button in main menu");
            var template = Instantiate(src, list).GetComponent<UIButton>();
            template.gameObject.SetActive(true);
            template.transform.localScale = Vector3.one;
            StripLocalization(template.gameObject);
            foreach (var t in template.GetComponentsInChildren<TMP_Text>(true)) t.gameObject.SetActive(true);
            var mbt = template.GetComponent<MenuButton>();
            if (mbt != null) DestroyImmediate(mbt);

            var tmpl = template.GetComponentInChildren<TMP_Text>(true);
            if (tmpl != null) { Font = tmpl.font; FontMaterial = tmpl.fontSharedMaterial; }

            // Title = the pause menu's header text. Status lines go at the top of the button list
            // (it has a VerticalLayoutGroup) so they never overlap the title.
            // includeInactive: the cloned menu is still inactive here
            var texts = go.GetComponentsInChildren<TMP_Text>(true).Where(t => t.GetComponentInParent<UIButton>(true) == null).ToList();
            if (texts.Count > 0)
            {
                menu._title = texts[0];
                for (int i = 1; i < texts.Count; i++) texts[i].gameObject.SetActive(false);
            }
            if (tmpl != null)
            {
                menu._status = CloneLabel(tmpl, "BFUMP_Status", 34f, template.transform.parent, 0);
                menu._players = CloneLabel(tmpl, "BFUMP_Players", 26f, template.transform.parent, 1);
            }

            template.name = "BFUMP_Btn0";
            menu._pool.Add(template);
            for (int i = 1; i < 9; i++)
            {
                var b = Instantiate(template.gameObject, template.transform.parent).GetComponent<UIButton>();
                b.name = "BFUMP_Btn" + i;
                menu._pool.Add(b);
            }
            for (int i = 0; i < menu._pool.Count; i++)
            {
                int idx = i;
                var b = menu._pool[i];
                b.onClick = new Button.ButtonClickedEvent();
                b.onClick.AddListener(() => menu.OnButton(idx));
                menu._actions.Add(null);
            }
            menu.initialSelection = template.gameObject;

            UiMirror.RegisterMenu(ui, menu);
            menu.Setup();

            if (Plugin.CfgDumpUi.Value) Dump(go.transform, 0);
        }

        static TMP_Text CloneLabel(TMP_Text src, string name, float size, Transform parent, int sibling)
        {
            var go = Instantiate(src.gameObject, parent);
            go.name = name;
            go.SetActive(true);
            var t = go.GetComponent<TMP_Text>();
            t.fontSize = size;
            t.enableAutoSizing = false;
            t.enableWordWrapping = true;
            t.alignment = TextAlignmentOptions.Center;
            t.raycastTarget = false;
            t.text = "";
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = size * 1.3f;
            le.preferredWidth = 1100f;
            go.transform.SetSiblingIndex(sibling);
            return t;
        }

        static void BuildMainButton(UIMenuMain main)
        {
            var src = main.initialSelection;
            if (src == null) { Plugin.Log.LogWarning("[OnlineMenu] main menu has no initial selection"); return; }
            var go = Instantiate(src, src.transform.parent);
            go.name = MainButtonName;
            go.transform.SetSiblingIndex(src.transform.GetSiblingIndex() + 1);
            StripLocalization(go);
            var text = go.GetComponentInChildren<TMP_Text>(true);
            if (text != null) text.text = "EN LIGNE";
            var mb = go.GetComponent<MenuButton>();
            if (mb != null && mb.helperText != null && !mb.helperText.transform.IsChildOf(go.transform)) mb.helperText = null;

            var btn = go.GetComponent<Button>();
            btn.onClick = new Button.ButtonClickedEvent();
            btn.onClick.AddListener(Open);

            // Keep explicit navigation consistent: Play ↕ Online ↕ (what was below Play)
            var srcSel = src.GetComponent<Selectable>();
            if (srcSel != null && srcSel.navigation.mode == Navigation.Mode.Explicit)
            {
                var below = srcSel.navigation.selectOnDown;
                var n = srcSel.navigation; n.selectOnDown = btn; srcSel.navigation = n;
                var m = btn.navigation; m.mode = Navigation.Mode.Explicit; m.selectOnUp = srcSel; m.selectOnDown = below;
                m.selectOnLeft = srcSel.navigation.selectOnLeft; m.selectOnRight = srcSel.navigation.selectOnRight;
                btn.navigation = m;
                if (below != null && below.navigation.mode == Navigation.Mode.Explicit)
                {
                    var b = below.navigation; b.selectOnUp = btn; below.navigation = b;
                }
            }
            if (Plugin.CfgDumpUi.Value) Dump(src.transform.parent, 0);
        }

        static void Dump(Transform t, int depth)
        {
            var comps = string.Join(",", t.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name));
            Plugin.Log.LogInfo(new string(' ', depth * 2) + t.name + " [" + comps + "]");
            if (depth < 6) for (int i = 0; i < t.childCount; i++) Dump(t.GetChild(i), depth + 1);
        }

        // ── Menu lifecycle ───────────────────────────────────────────────────────

        public static void Open()
        {
            var ui = Game.Get<UIManager>();
            if (ui == null || Instance == null) return;
            if (ui.GetCurrentMenu() == Instance) return;
            ui.PushMenu(Instance);
        }

        public override void Setup()
        {
            var w = transform.Find("Wrapper");
            if (w != null) wrapper = w.GetComponent<RectTransform>();
        }

        const float BackdropVeil = 0.55f;

        /// <summary>The main menu's illustration is drawn only while its camera runs (the lobby and the options stop it).</summary>
        static void ShowBackdrop()
        {
            var ui = Game.Get<UIManager>();
            if (ui != null && ui.mainMenuBGCamera != null) ui.mainMenuBGCamera.enabled = true;
        }

        public override void Resume()
        {
            base.Resume();
            ShowBackdrop();
        }

        public override void Init()
        {
            ShowBackdrop();
            if (_group != null) _group.alpha = 1f;
            _dirty = true;
            Refresh();
            base.Init();
            ZoomInWrapper();
            if (Session.Instance != null) { Session.Instance.Changed -= MarkDirty; Session.Instance.Changed += MarkDirty; }
            if (Plugin.CfgDumpUi.Value) StartCoroutine(DumpState());
        }

        System.Collections.IEnumerator DumpState()
        {
            yield return new WaitForSecondsRealtime(1f);
            foreach (var t in GetComponentsInChildren<RectTransform>(true))
            {
                if (t.name.StartsWith("TMP SubMesh")) continue;
                var cv = t.GetComponent<Canvas>();
                var cg = t.GetComponent<CanvasGroup>();
                var gr = t.GetComponent<Graphic>();
                Plugin.Log.LogInfo($"[UIState] {t.name} active={t.gameObject.activeSelf}/{t.gameObject.activeInHierarchy} scale={t.localScale} pos={t.anchoredPosition} size={t.rect.size}" +
                    (cv != null ? $" canvas={cv.enabled}/{cv.sortingOrder}/{cv.overrideSorting}" : "") +
                    (cg != null ? $" alpha={cg.alpha}" : "") + (gr != null ? $" gfx={gr.enabled}/{gr.color.a}" : ""));
            }
        }

        public override void Cleanup()
        {
            if (Session.Instance != null) Session.Instance.Changed -= MarkDirty;
            base.Cleanup();
        }

        void MarkDirty() => _dirty = true;

        void Update()
        {
            var ui = Game.Get<UIManager>();
            if (ui == null || ui.GetCurrentMenu() != this) { if (_typing) StopTyping(ui); return; }
            if (_typing) { UpdateTyping(ui); return; }
            if (ui.GetButtonDownFromEventSystem("UICancel")) { Back(); return; }
            if (Role == Role.None && Time.unscaledTime > _nextFriendScan)
            {
                _nextFriendScan = Time.unscaledTime + 3f;
                var f = SteamTransport.HostingFriends(3);
                if (!f.Select(x => x.host.m_SteamID).SequenceEqual(_friends.Select(x => x.host.m_SteamID))) { _friends = f; _dirty = true; }
            }
            if (Role == Role.None && Time.unscaledTime > _nextClipCheck)
            {
                // an address copied while this screen is open shows up right away
                _nextClipCheck = Time.unscaledTime + 0.75f;
                string clip = ClipboardAddress();
                if (clip != _lastClip) { _lastClip = clip; _dirty = true; }
            }
            if (_dirty) Refresh();
        }

        float _nextClipCheck;
        string _lastClip;

        // ── Typing an address with the keyboard ──────────────────────────────────

        bool _typing;
        string _typed = "";
        int _typingButton = -1;

        void StartTyping()
        {
            _typing = true;
            string last = Plugin.CfgLastIP.Value;
            _typed = last == "127.0.0.1" ? "" : last;
            var ui = Game.Get<UIManager>();
            if (ui != null && ui.eventSystem != null) ui.eventSystem.sendNavigationEvents = false; // keys type text, not navigate
            UpdateTypingLabel();
        }

        void StopTyping(UIManager ui)
        {
            _typing = false;
            if (ui != null && ui.eventSystem != null) ui.eventSystem.sendNavigationEvents = true;
            _dirty = true;
        }

        void UpdateTyping(UIManager ui)
        {
            if (Input.GetKeyDown(KeyCode.Escape)) { StopTyping(ui); return; }
            if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKeyDown(KeyCode.V))
            {
                string c = "";
                try { c = GUIUtility.systemCopyBuffer ?? ""; } catch { }
                var extracted = ExtractAddress(c);
                _typed = (_typed + (extracted ?? c.Trim())).Substring(0, Math.Min(64, (_typed + (extracted ?? c.Trim())).Length));
            }
            else
            {
                foreach (char ch in Input.inputString)
                {
                    if (ch == '\b') { if (_typed.Length > 0) _typed = _typed.Substring(0, _typed.Length - 1); }
                    else if (ch == '\n' || ch == '\r')
                    {
                        string addr = _typed.Trim();
                        StopTyping(ui);
                        if (addr.Length > 0) { JoinAddress(addr); Game.Get<AudioManager>()?.PlayOneShot("event:/ui/select"); }
                        return;
                    }
                    else if (_typed.Length < 64 && (char.IsLetterOrDigit(ch) || ch == '.' || ch == ':' || ch == '-')) _typed += ch;
                }
            }
            UpdateTypingLabel();
        }

        void UpdateTypingLabel()
        {
            if (_typingButton < 0 || _typingButton >= _pool.Count) return;
            var t = _pool[_typingButton].GetComponentInChildren<TMP_Text>(true);
            if (t == null) return;
            bool caret = (Time.unscaledTime * 2f) % 2f < 1f;
            t.text = "ADRESSE : " + _typed + (caret ? "_" : " ");
            if (_status != null) _status.text = "Tape l'adresse de l'hôte (ex. 203.0.113.10:7777)\nEntrée : rejoindre · Échap : annuler · Ctrl+V : coller";
        }

        static Role Role => Session.Instance != null ? Session.Instance.Role : Role.None;

        void Back()
        {
            var s = Session.Instance;
            if (s != null && s.Connecting) { s.Leave(); return; }
            Game.Get<AudioManager>()?.PlayOneShot("event:/ui/back");
            UIResponse_Close();
        }

        void OnButton(int i)
        {
            if (i < 0 || i >= _actions.Count) return;
            try { _actions[i]?.Invoke(); }
            catch (Exception e) { Plugin.Log.LogError("[OnlineMenu] action: " + e); }
            _dirty = true;
        }

        // ── Content ──────────────────────────────────────────────────────────────

        void Refresh()
        {
            _dirty = false;
            var s = Session.Instance;
            var entries = new List<(string label, Action act)>();
            string status;

            if (s == null || s.Role == Role.None)
            {
                status = string.IsNullOrEmpty(s?.Status) ? "Joue avec tes amis sur Internet, sans serveur." : s.Status;
                if (SteamTransport.Available)
                {
                    entries.Add(("HÉBERGER UNE PARTIE", () => s.HostSteam()));
                    foreach (var f in _friends)
                    {
                        var host = f.host;
                        entries.Add(("REJOINDRE " + f.name.ToUpperInvariant(), () => s.JoinSteamHost(host)));
                    }
                }
                entries.Add(("HÉBERGER EN IP DIRECTE", () => s.HostDirect(Plugin.CfgPort.Value)));
                string clip = ClipboardAddress();
                _lastClip = clip;
                if (clip != null) entries.Add(("REJOINDRE " + clip, () => JoinAddress(clip)));
                else if (Plugin.CfgLastIP.Value != "127.0.0.1") entries.Add(("REJOINDRE " + Plugin.CfgLastIP.Value, () => JoinAddress(Plugin.CfgLastIP.Value)));
                _typingButton = entries.Count;
                entries.Add(("SAISIR UNE ADRESSE…", StartTyping));
                entries.Add(("RETOUR", Back));
            }
            else if (s.Connecting)
            {
                status = s.Status;
                entries.Add(("ANNULER", () => s.Leave()));
            }
            else if (s.Role == Role.Host)
            {
                status = s.Status;
                entries.Add(("JOUER", StartLobby));
                if (s.Transport is SteamTransport st) entries.Add(("INVITER DES AMIS", () => { if (!st.OpenInviteOverlay()) s.Notify("Liste d'amis Steam : clic droit sur un ami → « Inviter à jouer »"); }));
                else entries.Add(("COPIER MON ADRESSE IP", CopyLocalIp));
                entries.Add(("QUITTER LA SESSION", () => s.Leave()));
                entries.Add(("RETOUR", Back));
            }
            else
            {
                status = s.Status + "\nL'hôte choisit le mode et lance la partie.";
                entries.Add(("QUITTER LA SESSION", () => s.Leave()));
            }

            if (_title != null) _title.text = "EN LIGNE";
            if (_status != null) _status.text = status;
            if (_players != null) _players.text = PlayersLine(s);

            Selectable prev = null;
            var visible = new List<UIButton>();
            for (int i = 0; i < _pool.Count; i++)
            {
                var b = _pool[i];
                bool on = i < entries.Count;
                b.gameObject.SetActive(on);
                _actions[i] = on ? entries[i].act : null;
                if (!on) continue;
                var t = b.GetComponentInChildren<TMP_Text>(true);
                if (t != null) t.text = entries[i].label;
                visible.Add(b);
            }
            for (int i = 0; i < visible.Count; i++)
            {
                var nav = new Navigation
                {
                    mode = Navigation.Mode.Explicit,
                    selectOnUp = i > 0 ? visible[i - 1] : visible[visible.Count - 1],
                    selectOnDown = i < visible.Count - 1 ? visible[i + 1] : visible[0],
                };
                visible[i].navigation = nav;
                prev = visible[i];
            }
            initialSelection = visible.Count > 0 ? visible[0].gameObject : null;

            var ui = Game.Get<UIManager>();
            var sel = ui != null && ui.eventSystem != null ? ui.eventSystem.currentSelectedGameObject : null;
            if (ui != null && ui.GetCurrentMenu() == this && (sel == null || !sel.activeInHierarchy || !Owns(sel)) && initialSelection != null)
                ui.SetSelectedGameObject(initialSelection);
        }

        static string PlayersLine(Session s)
        {
            if (s == null || !Session.Active) return "";
            var parts = new List<string>();
            for (int i = 0; i < s.Slots.Length; i++)
                parts.Add(s.Slots[i].Used ? $"{i + 1}. {s.Slots[i].Name}" : $"{i + 1}. —");
            return string.Join("    ", parts);
        }

        static void StartLobby()
        {
            var ui = Game.Get<UIManager>();
            var lobby = ui?.GetMenu(UIMenuBase.MenuType.Lobby) as UIMenuLobby;
            if (lobby == null) return;
            ui.PushMenu(lobby);
            lobby.ResetAllPlayers();
        }

        static string ClipboardAddress()
        {
            string c;
            try { c = GUIUtility.systemCopyBuffer; } catch { return null; }
            return ExtractAddress(c);
        }

        /// <summary>First "a.b.c.d" or "a.b.c.d:port" found in a text (e.g. a whole chat message).</summary>
        static string ExtractAddress(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 500) return null;
            var m = System.Text.RegularExpressions.Regex.Match(text, @"\b(\d{1,3}(?:\.\d{1,3}){3})(?::(\d{1,5}))?\b");
            if (!m.Success || !IPAddress.TryParse(m.Groups[1].Value, out _)) return null;
            if (m.Groups[1].Value == "127.0.0.1") return null; // never useful to a friend
            return m.Groups[2].Success ? m.Groups[1].Value + ":" + m.Groups[2].Value : m.Groups[1].Value;
        }

        static void JoinAddress(string address)
        {
            string host = address;
            int port = Plugin.CfgPort.Value;
            int colon = address.LastIndexOf(':');
            if (colon > 0 && int.TryParse(address.Substring(colon + 1), out int p)) { host = address.Substring(0, colon); port = p; }
            Plugin.CfgLastIP.Value = address;
            Session.Instance.JoinDirect(host, port);
        }

        static void CopyLocalIp()
        {
            // Internet address (port opened on the router by UPnP) first: works for friends anywhere.
            var pub = Session.Instance?.PublicAddress;
            if (!string.IsNullOrEmpty(pub))
            {
                GUIUtility.systemCopyBuffer = pub;
                Session.Instance.Notify("Adresse Internet copiée : " + pub);
                return;
            }
            var ips = new List<string>();
            try
            {
                foreach (var a in Dns.GetHostAddresses(Dns.GetHostName()))
                    if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ips.Add(a.ToString());
            }
            catch { }
            // No Internet address: local network address (same box), then VPN-style ones
            string best = ips.FirstOrDefault(i => i.StartsWith("192.168.") || i.StartsWith("10."))
                          ?? ips.FirstOrDefault(i => !i.StartsWith("127.") && !i.StartsWith("169.254.") && !i.StartsWith("172."))
                          ?? "127.0.0.1";
            string text = best + ":" + Plugin.CfgPort.Value;
            GUIUtility.systemCopyBuffer = text;
            Session.Instance?.Notify("Adresse du réseau local copiée : " + text + " (Internet : port non ouvert)");
        }
    }
}
