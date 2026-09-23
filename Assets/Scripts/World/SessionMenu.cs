using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VRZ.Network;

namespace VRZ.World
{
    /// Lobby-scene menu for creating or joining a room (netcode fix B2).
    ///
    /// Talks only to NetworkManager: reads State / Sessions / CurrentCode / LastError, and calls
    /// CreateSession() or JoinSession(name). Never touches the runner. Hides itself once the
    /// session is connected; the rest of the lobby flow (LobbyManager, tutorial) is unchanged.
    ///
    /// Scene wiring (all optional except panelRoot): a world-space Canvas with
    ///   - createButton  ("Create Lobby")
    ///   - joinButton    ("Join Lobby")  -> shows the room list
    ///   - backButton    (list -> main)
    ///   - listRoot      (VerticalLayoutGroup) + rowPrefab (Button with a TMP_Text child)
    ///   - codeText      (big "YOUR ROOM: 42" once created)
    ///   - statusText    ("Searching rooms...", errors)
    public class SessionMenu : MonoBehaviour
    {
        [Header("Roots")]
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private GameObject mainPage;
        [SerializeField] private GameObject listPage;

        [Header("Main page")]
        [SerializeField] private Button createButton;
        [SerializeField] private Button joinButton;
        [SerializeField] private TMP_Text statusText;

        [Header("List page")]
        [SerializeField] private Button backButton;
        [SerializeField] private Transform listRoot;
        [SerializeField] private GameObject rowPrefab;
        [SerializeField] private TMP_Text emptyListText;

        [Header("Connected")]
        [SerializeField] private TMP_Text codeText;

        [Header("Input")]
        [Tooltip("Hand pointers (HandCanvasPointer objects) enabled only while the menu is visible, so the trigger still grabs normally afterwards.")]
        [SerializeField] private GameObject[] pointers;

        [Header("Hidden while the menu is open")]
        [Tooltip("Lobby props that only get in the way before a room exists (tutorial signs, rifles, magazines). Their Renderers and Colliders are switched off while the menu is visible and restored to their ORIGINAL state on connect. Objects are never deactivated: scene NetworkObjects must stay registered with Fusion.")]
        [SerializeField] private GameObject[] hiddenWhileMenu;

        private readonly System.Collections.Generic.Dictionary<Renderer, bool> _hiddenOriginal = new();
        private readonly System.Collections.Generic.Dictionary<Collider, bool> _hiddenColliderOriginal = new();
        private bool _propsHidden;

        private void SetPropsHidden(bool hide)
        {
            if (hide == _propsHidden || hiddenWhileMenu == null) return;
            _propsHidden = hide;
            if (hide)
            {
                _hiddenOriginal.Clear(); _hiddenColliderOriginal.Clear();
                foreach (var go in hiddenWhileMenu)
                {
                    if (go == null) continue;
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true)) { _hiddenOriginal[r] = r.enabled; r.enabled = false; }
                    foreach (var c in go.GetComponentsInChildren<Collider>(true)) { _hiddenColliderOriginal[c] = c.enabled; c.enabled = false; }
                }
            }
            else
            {
                foreach (var kv in _hiddenOriginal) if (kv.Key != null) kv.Key.enabled = kv.Value;
                foreach (var kv in _hiddenColliderOriginal) if (kv.Key != null) kv.Key.enabled = kv.Value;
                _hiddenOriginal.Clear(); _hiddenColliderOriginal.Clear();
            }
        }

        private NetworkManager _nm;
        private readonly List<GameObject> _rows = new();
        private bool _showingList;

        private void OnEnable()
        {
            if (createButton) createButton.onClick.AddListener(OnCreateClicked);
            if (joinButton) joinButton.onClick.AddListener(OnJoinClicked);
            if (backButton) backButton.onClick.AddListener(OnBackClicked);
            TryBind();
            Refresh();
        }

        private void OnDisable()
        {
            if (createButton) createButton.onClick.RemoveListener(OnCreateClicked);
            if (joinButton) joinButton.onClick.RemoveListener(OnJoinClicked);
            if (backButton) backButton.onClick.RemoveListener(OnBackClicked);
            Unbind();
        }

        private void Update()
        {
            // The manager can be replaced (game over, disconnect) and it is created in Awake of
            // the same scene, so binding lazily is simpler than depending on script order.
            if (_nm == null || _nm != NetworkManager.instance) { Unbind(); TryBind(); Refresh(); }
        }

        private void TryBind()
        {
            _nm = NetworkManager.instance;
            if (_nm == null) return;
            _nm.OnSessionsChanged += Refresh;
            _nm.OnStateChanged += OnStateChanged;
        }

        private void Unbind()
        {
            if (_nm == null) return;
            _nm.OnSessionsChanged -= Refresh;
            _nm.OnStateChanged -= OnStateChanged;
            _nm = null;
        }

        private void OnStateChanged(NetworkManager.SessionState _) => Refresh();

        // ── Buttons ──────────────────────────────────────────────────────────────────────

        private void OnCreateClicked()
        {
            if (_nm == null) return;
            _nm.CreateSession();
            Refresh();
        }

        private void OnJoinClicked()
        {
            _showingList = true;
            Refresh();
        }

        private void OnBackClicked()
        {
            _showingList = false;
            Refresh();
        }

        private void OnRowClicked(string sessionName)
        {
            if (_nm == null) return;
            _nm.JoinSession(sessionName);
            Refresh();
        }

        // ── Rendering ────────────────────────────────────────────────────────────────────

        private void Refresh()
        {
            var state = _nm != null ? _nm.State : NetworkManager.SessionState.Offline;
            bool connected = state == NetworkManager.SessionState.Connected;

            if (panelRoot) panelRoot.SetActive(!connected);
            if (pointers != null) foreach (var p in pointers) if (p) p.SetActive(!connected);
            SetPropsHidden(!connected);
            if (connected)
            {
                if (codeText) { codeText.gameObject.SetActive(true); codeText.text = "ROOM  " + _nm.CurrentCode; }
                return;
            }
            if (codeText) codeText.gameObject.SetActive(false);

            bool browsing = state == NetworkManager.SessionState.BrowsingLobby;
            bool busy = state == NetworkManager.SessionState.Connecting;

            if (mainPage) mainPage.SetActive(!_showingList);
            if (listPage) listPage.SetActive(_showingList);
            if (createButton) createButton.interactable = browsing;
            if (joinButton) joinButton.interactable = browsing;

            if (statusText)
            {
                statusText.text = state switch
                {
                    NetworkManager.SessionState.Offline => "Starting...",
                    NetworkManager.SessionState.BrowsingLobby => "",
                    NetworkManager.SessionState.Connecting => "Connecting...",
                    NetworkManager.SessionState.Failed => NetworkManager.LastError,
                    _ => ""
                };
            }

            if (_showingList) RebuildList(browsing && !busy);
        }

        private void RebuildList(bool clickable)
        {
            foreach (var r in _rows) if (r) Destroy(r);
            _rows.Clear();

            var sessions = _nm != null ? _nm.Sessions : null;
            int count = sessions != null ? sessions.Count : 0;
            if (emptyListText) emptyListText.gameObject.SetActive(count == 0);
            if (listRoot == null || rowPrefab == null || sessions == null) return;

            foreach (var s in sessions)
            {
                var row = Instantiate(rowPrefab, listRoot);
                var code = s.Name.StartsWith(NetworkManager.SessionPrefix)
                    ? s.Name.Substring(NetworkManager.SessionPrefix.Length) : s.Name;
                var label = row.GetComponentInChildren<TMP_Text>();
                if (label) label.text = $"ROOM {code}    {s.PlayerCount}/{s.MaxPlayers}";
                var btn = row.GetComponent<Button>();
                if (btn)
                {
                    var name = s.Name;   // capture per row
                    btn.interactable = clickable;
                    btn.onClick.AddListener(() => OnRowClicked(name));
                }
                _rows.Add(row);
            }
        }
    }
}
