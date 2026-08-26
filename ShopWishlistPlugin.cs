using System.Diagnostics;
// V0.25.45: native TAG_RACE rail summary, stable self row, fixed drag ghost.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Web.Script.Serialization;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Plugins;
using HearthDb;
using HearthDb.Enums;
using ScryDotNet;
using HDTCore = Hearthstone_Deck_Tracker.Core;
using IOPath = System.IO.Path;

namespace HDTShopWishlist
{
    public sealed class ShopWishlistPlugin : IPlugin
    {
        private const string PluginName = "Shop Wishlist Overlay";
        private readonly WishlistStore _store = new WishlistStore();
        private WishlistOverlayWindow _overlay;
        private WishlistWindow _settings;
        private InGameLauncherWindow _launcher;
        private BattlegroundsLobbyInfoWindow _lobbyInfo;
        private bool _enabled;
        private DateTime _lastShopSeen = DateTime.MinValue;
        private DateTime _lastRefresh = DateTime.MinValue;
        private DateTime _lastLobbyTribeRefresh = DateTime.MinValue;
        private volatile string _pendingUpdatePayload;
        private DateTime _updateSafeSince = DateTime.MinValue;

        public void OnLoad()
        {
            // Card art (both the full card download and the trimmed art-only download used by
            // the comp builder pool) is fetched over HTTPS via WebClient. .NET Framework 4.7.2
            // does not always default to TLS 1.2 depending on the machine's Windows/.NET config,
            // and a modern CDN can silently reject an older handshake - the download then fails
            // quietly (only Debug.WriteLine), and the builder falls back to a plainer local crop
            // of the card image instead of the nicer downloaded art. Force TLS 1.2 (and 1.3 where
            // supported) up front so art downloads work the same way on every machine.
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { } // Tls13, if the OS supports it
            }
            catch { }
            _enabled = true;
            _store.Load();
            _overlay = new WishlistOverlayWindow(_store, ToggleBuilder);
            _overlay.Show();
            _launcher = new InGameLauncherWindow(_store, ToggleBuilder);
            _launcher.Show();
            _lobbyInfo = new BattlegroundsLobbyInfoWindow();
            _lobbyInfo.Show();

            // Records whether a previous Skip Combat left Hearthstone firewalled, then clears it.
            BattlegroundsLobbyInfoWindow.LogSkipCombatStartupState();

            AutoUpdater.CheckAndPrepareAsync(Version, delegate (string payloadDir) { _pendingUpdatePayload = payloadDir; });
        }

        public void OnUnload()
        {
            // First, before anything can throw: never leave the game blocked because the plugin
            // was unloaded mid-run. This does not cover a hard kill or a crash - the startup
            // check above is what catches those.
            BattlegroundsLobbyInfoWindow.CleanupSkipCombatRule("plugin unload");
            _enabled = false;
            try { if (_settings != null) _settings.Close(); } catch { }
            _settings = null;
            try { if (_launcher != null) _launcher.Close(); } catch { }
            _launcher = null;
            try { if (_lobbyInfo != null) _lobbyInfo.Close(); } catch { }
            _lobbyInfo = null;
            try { if (_overlay != null) _overlay.Close(); } catch { }
            _overlay = null;
        }

        public void OnButtonPress()
        {
            ToggleBuilder();
        }

        private void ToggleBuilder()
        {
            if (IsBattlegroundsGame())
            {
                ToggleInGameBuilder();
            }
            else
            {
                ToggleDesktopSettings();
            }
        }

        internal static bool IsGoldenBattlegroundsVariant(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            string s = id.Trim();
            return s.EndsWith("_G", StringComparison.OrdinalIgnoreCase)
                || s.EndsWith("_GOLDEN", StringComparison.OrdinalIgnoreCase)
                || s.IndexOf("_G_", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("_GOLDEN_", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("_G_TRIPLE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsBattlegroundsGame()
        {
            try { return HDTCore.Game != null && PluginReflection.GetBool(HDTCore.Game, "IsBattlegroundsMatch") && FindHearthstoneWindow(out _, out _); }
            catch { return false; }
        }

        private void ToggleDesktopSettings()
        {
            if (_settings == null)
            {
                _settings = new WishlistWindow(_store, delegate { if (_overlay != null) _overlay.RefreshNow(); });
                _settings.Closed += delegate { _settings = null; };
                _settings.Show();
            }
            else
            {
                if (_settings.Visibility != Visibility.Visible) _settings.Show();
                _settings.Activate();
            }
        }

        private void ToggleInGameBuilder()
        {
            if (!FindHearthstoneWindow(out var rect, out _))
            {
                ToggleDesktopSettings();
                return;
            }
            if (_settings == null)
            {
                _settings = new WishlistWindow(_store, delegate { if (_overlay != null) _overlay.RefreshNow(); });
                _settings.Closed += delegate { _settings = null; };
                _settings.PrepareInGame(rect);
                _settings.SuppressDeactivateFor(450);
                _settings.Show();
                _settings.Activate();
            }
            else
            {
                if (!_settings.IsInGameMode)
                {
                    _settings.PrepareInGame(rect);
                    if (_settings.Visibility != Visibility.Visible) _settings.Show();
                }
                else
                {
                    if (_settings.Visibility == Visibility.Visible) _settings.Hide();
                    else { _settings.SuppressDeactivateFor(300); _settings.Show(); _settings.Activate(); }
                }
            }
        }

        private static bool FindHearthstoneWindow(out Rect rect, out IntPtr handle)
        {
            foreach (Process p in Process.GetProcessesByName("Hearthstone"))
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    Native.RECT r;
                    if (Native.GetWindowRect(p.MainWindowHandle, out r))
                    {
                        rect = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                        handle = p.MainWindowHandle;
                        return true;
                    }
                }
                catch { }
            }
            rect = Rect.Empty; handle = IntPtr.Zero; return false;
        }

        private bool HasActiveShopContext(object game)
        {
            try
            {
                if (game == null || !PluginReflection.GetBool(game, "IsBattlegroundsMatch")) return false;
                foreach (object e in PluginReflection.EnumerateEntities(game))
                {
                    string id = PluginReflection.GetString(e, "CardId");
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    if (PluginReflection.GetBool(e, "IsHero")) continue;
                    if (PluginReflection.GetBool(e, "IsPlayer") || PluginReflection.GetBool(e, "IsOpponent")) continue;
                    if (PluginReflection.GetZoneTagValue(e) != 1) continue;
                    int pos = PluginReflection.GetInt(e, "ZonePosition");
                    if (pos >= 1 && pos <= 7 && !PluginReflection.GetBool(e, "IsInSetAside")) return true;
                }
            }
            catch { }
            return false;
        }

        public void OnUpdate()
        {
            if (!_enabled || _overlay == null) return;
            if ((DateTime.UtcNow - _lastRefresh).TotalMilliseconds < 100) return;
            _lastRefresh = DateTime.UtcNow;
            try
            {
                object game = HDTCore.Game;
                bool bgMatch = false;
                try { bgMatch = game != null && PluginReflection.GetBool(game, "IsBattlegroundsMatch"); } catch { bgMatch = false; }

                if (_pendingUpdatePayload != null)
                {
                    // Don't yank HDT away mid-match. Only apply once we've been out of a BG
                    // match for a little while (covers the post-game screen too).
                    if (bgMatch) { _updateSafeSince = DateTime.MinValue; }
                    else
                    {
                        if (_updateSafeSince == DateTime.MinValue) _updateSafeSince = DateTime.UtcNow;
                        else if ((DateTime.UtcNow - _updateSafeSince).TotalSeconds >= 15)
                        {
                            string payload = _pendingUpdatePayload;
                            _pendingUpdatePayload = null;
                            AutoUpdater.ApplyAndRestart(payload);
                            return;
                        }
                    }
                }

                bool hearthstoneForeground = Native.IsForegroundHearthstone();
                if (!hearthstoneForeground)
                {
                    _overlay.HideForExternalFocus();
                }
                else
                {
                    _overlay.UpdateFromGame(game);
                }
                if (_launcher != null) _launcher.UpdateForCurrentGame(game);
                if (_lobbyInfo != null) _lobbyInfo.UpdateForCurrentGame(game);

                bool hasShop = bgMatch && hearthstoneForeground && HasActiveShopContext(game);
                if (!bgMatch)
                {
                    _lastShopSeen = DateTime.MinValue;
                    if (_settings != null && _settings.IsInGameMode)
                    {
                        try { _settings.Hide(); } catch { }
                    }
                }
                else if (hasShop)
                {
                    _lastShopSeen = DateTime.UtcNow;
                }
                if (_settings != null && _settings.IsInGameMode && (DateTime.UtcNow - _lastLobbyTribeRefresh).TotalMilliseconds >= 1000)
                {
                    _settings.RefreshLobbyTribes(game);
                    _lastLobbyTribeRefresh = DateTime.UtcNow;
                }
            }
            catch (Exception ex) { Debug.WriteLine("Shop Wishlist: " + ex); }
        }

        public string Name { get { return PluginName; } }
        public string Description { get { return "Visual Battlegrounds comp builder + live shop wishlist highlight + in-game comp panel."; } }
        public string ButtonText { get { return "Open / Toggle Comp Builder"; } }
        public string Author { get { return "Ylan Benainous"; } }
        public Version Version { get { return new Version(0, 30, 0); } }
        public MenuItem MenuItem
        {
            get
            {
                var menu = new MenuItem { Header = PluginName };
                var check = new MenuItem { Header = "Check for Updates..." };
                check.Click += delegate
                {
                    AutoUpdater.CheckAndPrepareAsync(Version, delegate (string payloadDir)
                    {
                        _pendingUpdatePayload = payloadDir;
                        _updateSafeSince = DateTime.MinValue;
                    });
                };
                menu.Items.Add(check);
                var support = new MenuItem { Header = "♥ Support on PayPal" };
                support.Click += delegate { AutoUpdater.OpenPayPalSupport(); };
                menu.Items.Add(support);
                return menu;
            }
        }
    }

    internal sealed class WishlistStore
    {
        private readonly string _dir;
        internal const int MaxComps = 8;
        private readonly string[] _compPaths = new string[MaxComps];
        private readonly Dictionary<string, int>[] _priority = new Dictionary<string, int>[MaxComps];
        private readonly string[] _compNames = new string[MaxComps];
        private int _compCount = 3;
        private int _activeComp;
        public readonly string ProbePath;

        public WishlistStore()
        {
            _dir = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HDTShopWishlist");
            Directory.CreateDirectory(_dir);
            for (int i = 0; i < MaxComps; i++)
            {
                _compPaths[i] = IOPath.Combine(_dir, "comp" + (i + 1) + ".txt");
                _priority[i] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                _compNames[i] = "Comp " + (i + 1);
            }
            ProbePath = IOPath.Combine(_dir, "last-bgs-entities.txt");
        }

        public int ActiveCompIndex { get { return _activeComp; } }
        public int CompCount { get { return _compCount; } }
        public IEnumerable<string> ActiveIds { get { return _priority[_activeComp].Keys; } }
        public string GetCompName(int index) { return index >= 0 && index < _compCount && !string.IsNullOrWhiteSpace(_compNames[index]) ? _compNames[index] : "Comp " + (index + 1); }

        public void Load()
        {
            string countPath = IOPath.Combine(_dir, "comp-count.txt");
            int storedCount;
            _compCount = 3;
            if (int.TryParse(File.Exists(countPath) ? File.ReadAllText(countPath) : "3", out storedCount)) _compCount = Math.Max(3, Math.Min(MaxComps, storedCount));
            for (int i = 0; i < MaxComps; i++)
            {
                _priority[i].Clear();
                string namePath = IOPath.Combine(_dir, "comp-name-" + (i + 1) + ".txt");
                try { if (File.Exists(namePath)) { string n = File.ReadAllText(namePath).Trim(); if (!string.IsNullOrWhiteSpace(n)) _compNames[i] = n; } } catch { }
                if (!File.Exists(_compPaths[i])) continue;
                foreach (string line in SafeReadLines(_compPaths[i]))
                {
                    string v = (line ?? string.Empty).Trim();
                    if (v.Length == 0 || v.StartsWith("#")) continue;
                    string[] parts = v.Split('|');
                    int p = 1;
                    int parsed;
                    if (parts.Length > 1 && int.TryParse(parts[1], out parsed)) p = ClampPriority(parsed);
                    _priority[i][parts[0]] = p;
                }
            }
            string active = IOPath.Combine(_dir, "active.txt");
            int a;
            if (int.TryParse(File.Exists(active) ? File.ReadAllText(active) : "0", out a) && a >= 0 && a < _compCount) _activeComp = a; else _activeComp = 0;
        }

        private static IEnumerable<string> SafeReadLines(string path)
        {
            if (!File.Exists(path)) return Enumerable.Empty<string>();
            try { return File.ReadAllLines(path); } catch { return Enumerable.Empty<string>(); }
        }

        public static int ClampPriority(int p) { return p < 1 ? 1 : (p > 3 ? 3 : p); }

        public IEnumerable<string> GetCompIds(int index)
        {
            if (index < 0 || index >= _compCount) return Enumerable.Empty<string>();
            return _priority[index].Keys.ToArray();
        }

        public int GetPriority(int index, string id)
        {
            int p;
            return index >= 0 && index < _compCount && id != null && _priority[index].TryGetValue(id, out p) ? p : 0;
        }

        public int GetActivePriority(string id) { return GetPriority(_activeComp, id); }

        // Safety net + forensic trail for a reported bug where a card's Core/Important/Optional
        // placement appears to revert unexpectedly. Before every overwrite, snapshot the comp
        // file's CURRENT on-disk content (i.e. the state right before this write) with a
        // timestamp, so a bad write can be diagnosed after the fact and, if needed, restored.
        // Keeps only the most recent BackupsToKeep snapshots per comp to avoid unbounded growth.
        private const int BackupsToKeep = 20;
        private void BackupCompFile(int index)
        {
            try
            {
                if (index < 0 || index >= MaxComps || !File.Exists(_compPaths[index])) return;
                string backupDir = IOPath.Combine(_dir, "Backups");
                Directory.CreateDirectory(backupDir);
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
                string backupPath = IOPath.Combine(backupDir, "comp" + (index + 1) + "_" + stamp + ".txt");
                File.Copy(_compPaths[index], backupPath, true);

                var existing = Directory.GetFiles(backupDir, "comp" + (index + 1) + "_*.txt")
                    .OrderByDescending(f => f, StringComparer.Ordinal).ToList();
                for (int i = BackupsToKeep; i < existing.Count; i++)
                {
                    try { File.Delete(existing[i]); } catch { }
                }
            }
            catch { }
        }

        public void SetCardPriority(int index, string id, int priority)
        {
            if(index < 0 || index >= _compCount || string.IsNullOrWhiteSpace(id)) return;
            BackupCompFile(index);
            string key=id.Trim();
            _priority[index][key] = ClampPriority(priority);
            try
            {
                File.WriteAllLines(_compPaths[index], _priority[index].Select(kv => kv.Key + "|" + kv.Value));
                File.WriteAllText(IOPath.Combine(_dir, "comp-count.txt"), _compCount.ToString());
            }
            catch { }
        }

        public void SaveComp(int index, IEnumerable<Tuple<string,int>> items)
        {
            if (index < 0 || index >= _compCount) return;
            BackupCompFile(index);
            // Materialise BEFORE clearing. Callers build items as a lazy LINQ projection over this
            // very dictionary - e.g. GetCompIds(i).Select(id => Tuple.Create(id, GetPriority(i, id))).
            // GetCompIds() is eager (.ToArray()), but the Select is not: without this ToList the
            // GetPriority calls would run during the loop below, i.e. after Clear(), miss every
            // lookup, return 0, and ClampPriority(0) would rewrite every card as Core. That is
            // exactly how a comp's Core/Important/Optional flattened to all-Core on switching tabs
            // or removing a card, while the card list itself survived.
            var snapshot = (items ?? Enumerable.Empty<Tuple<string,int>>()).ToList();
            _priority[index].Clear();
            foreach (Tuple<string,int> t in snapshot)
            {
                if (t == null || string.IsNullOrWhiteSpace(t.Item1)) continue;
                _priority[index][t.Item1.Trim()] = ClampPriority(t.Item2);
            }
            File.WriteAllLines(_compPaths[index], _priority[index].Select(kv => kv.Key + "|" + kv.Value));
            try { File.WriteAllText(IOPath.Combine(_dir, "comp-count.txt"), _compCount.ToString()); } catch { }
        }

        public void MoveCard(int index, string id, int targetIndex)
        {
            if (index < 0 || index >= _compCount || string.IsNullOrWhiteSpace(id)) return;
            var ordered = _priority[index].Keys.ToList();
            int from = ordered.FindIndex(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
            if (from < 0) return;
            targetIndex = Math.Max(0, Math.Min(ordered.Count - 1, targetIndex));
            if (from == targetIndex) return;
            string item = ordered[from];
            ordered.RemoveAt(from);
            ordered.Insert(targetIndex, item);
            var rebuilt = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in ordered) rebuilt[key] = _priority[index][key];
            _priority[index] = rebuilt;
            BackupCompFile(index);
            try { File.WriteAllLines(_compPaths[index], _priority[index].Select(kv => kv.Key + "|" + kv.Value)); } catch { }
        }

        public void MoveCardRelative(int index, string id, int delta)
        {
            if (index < 0 || index >= _compCount || string.IsNullOrWhiteSpace(id)) return;
            var ordered = _priority[index].Keys.ToList();
            int from = ordered.FindIndex(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
            if (from < 0) return;
            int target = Math.Max(0, Math.Min(ordered.Count - 1, from + delta));
            MoveCard(index, id, target);
        }

        public int DuplicateComp(int sourceIndex, string requestedName)
        {
            if (sourceIndex < 0 || sourceIndex >= _compCount || _compCount >= MaxComps) return -1;
            int target = _compCount++;
            _compNames[target] = string.IsNullOrWhiteSpace(requestedName) ? GetCompName(sourceIndex) + " Copy" : requestedName.Trim();
            _priority[target] = new Dictionary<string,int>(_priority[sourceIndex], StringComparer.OrdinalIgnoreCase);
            try
            {
                File.WriteAllLines(_compPaths[target], _priority[target].Select(kv => kv.Key + "|" + kv.Value));
                File.WriteAllText(IOPath.Combine(_dir, "comp-name-" + (target + 1) + ".txt"), _compNames[target]);
                File.WriteAllText(IOPath.Combine(_dir, "comp-count.txt"), _compCount.ToString());
            } catch { }
            return target;
        }

        public bool DeleteComp(int index)
        {
            if (index < 0 || index >= _compCount || _compCount <= 3) return false;
            string path = _compPaths[index];
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            for (int i=index; i<_compCount-1; i++)
            {
                _priority[i] = _priority[i+1];
                _compNames[i] = _compNames[i+1];
                try
                {
                    File.WriteAllLines(_compPaths[i], _priority[i].Select(kv => kv.Key + "|" + kv.Value));
                    File.WriteAllText(IOPath.Combine(_dir, "comp-name-" + (i+1) + ".txt"), _compNames[i]);
                } catch { }
            }
            _compCount--;
            _compNames[_compCount] = "Comp " + (_compCount+1);
            _priority[_compCount] = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                File.WriteAllText(IOPath.Combine(_dir, "comp-count.txt"), _compCount.ToString());
                string lastName = IOPath.Combine(_dir, "comp-name-" + (_compCount+1) + ".txt");
                if (File.Exists(lastName)) File.Delete(lastName);
            } catch { }
            if (_activeComp >= _compCount) _activeComp = _compCount-1;
            return true;
        }

        public void SetActiveComp(int index)
        {
            if (index < 0 || index >= _compCount) return;
            _activeComp = index;
            try { File.WriteAllText(IOPath.Combine(_dir, "active.txt"), index.ToString()); } catch { }
        }

        public int AddComp(string requestedName)
        {
            if (_compCount >= MaxComps) return -1;
            int index = _compCount++;
            _compNames[index] = string.IsNullOrWhiteSpace(requestedName) ? "Comp " + (index + 1) : requestedName.Trim();
            try
            {
                File.WriteAllText(IOPath.Combine(_dir, "comp-count.txt"), _compCount.ToString());
                File.WriteAllText(IOPath.Combine(_dir, "comp-name-" + (index + 1) + ".txt"), _compNames[index]);
            }
            catch { }
            return index;
        }

        public void RenameComp(int index, string requestedName)
        {
            if (index < 0 || index >= _compCount || string.IsNullOrWhiteSpace(requestedName)) return;
            _compNames[index] = requestedName.Trim();
            try { File.WriteAllText(IOPath.Combine(_dir, "comp-name-" + (index + 1) + ".txt"), _compNames[index]); } catch { }
        }







        public Point LoadLauncherPosition()
        {
            try
            {
                string path = IOPath.Combine(_dir, "launcher-position.txt");
                if (File.Exists(path))
                {
                    string[] p = File.ReadAllText(path).Trim().Split(',');
                    double x, y;
                    if (p.Length >= 2 && double.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x) &&
                        double.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y))
                        return new Point(Math.Max(0.0, Math.Min(0.94, x)), Math.Max(0.0, Math.Min(0.94, y)));
                }
            } catch { }
            return new Point(0.028, 0.82);
        }

        public void SaveLauncherPosition(Point p)
        {
            try
            {
                string path = IOPath.Combine(_dir, "launcher-position.txt");
                File.WriteAllText(path, p.X.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + p.Y.ToString(System.Globalization.CultureInfo.InvariantCulture));
            } catch { }
        }

        public List<string> LoadTribeOrder(IEnumerable<string> defaults)
        {
            var result=new List<string>();
            string path=IOPath.Combine(_dir,"tribe-order.txt");
            if(File.Exists(path))
            {
                foreach(string line in SafeReadLines(path))
                {
                    string v=(line??string.Empty).Trim();
                    if(v.Length>0 && !result.Contains(v,StringComparer.OrdinalIgnoreCase)) result.Add(v);
                }
            }
            foreach(string d in defaults??Enumerable.Empty<string>()) if(!result.Contains(d,StringComparer.OrdinalIgnoreCase)) result.Add(d);
            return result;
        }

        public void SaveTribeOrder(IEnumerable<string> order)
        {
            try{File.WriteAllLines(IOPath.Combine(_dir,"tribe-order.txt"),(order??Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase));}catch{}
        }
    }

    internal sealed class WishlistOverlayWindow : Window
    {
        private readonly WishlistStore _store;
        private readonly Canvas _canvas = new Canvas();
        private readonly List<Grid> _boxes = new List<Grid>();
        private readonly List<DropShadowEffect> _glows = new List<DropShadowEffect>();
        private const int MaxSlots = 7;

        private readonly Action _toggleSettings;
        private readonly string[] _slotVisualState = new string[MaxSlots];
        private readonly string[] _slotEntityKey = new string[MaxSlots];
        private (int left,int top,int width,int height) _stableRect;
        private bool _hasStableRect;

        // Calibration aid (Ctrl+Shift+G): draws every shop slot's computed box, not just the
        // highlighted one, with its index - so a single screenshot shows the whole row's
        // alignment at once instead of needing one lucky screenshot per slot.
        private bool _debugSlotsEnabled;
        private readonly List<Border> _debugBoxes = new List<Border>();
        private readonly List<TextBlock> _debugLabels = new List<TextBlock>();
        // The ShopCards shape dump that used to live here was investigation tooling for finding a
        // screen-space Rect on HDT's pinning view model. Answered: there is none - only CardId,
        // IsSlotOccupied, TribeIconRace and an unrelated RecommendedSectionCanvasLeft. It ran
        // automatically and wrote to %TEMP% every session, so it is gone. The opt-in hotkey tools
        // (Ctrl+Shift+G slot grid, Ctrl+Shift+M class scan, Ctrl+Shift+F field dump) are kept.

        public WishlistOverlayWindow(WishlistStore store, Action toggleSettings)
        {
            _store = store;
            _toggleSettings = toggleSettings;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; ShowActivated = false; Topmost = true; IsHitTestVisible = false;
            ResizeMode = ResizeMode.NoResize; Content = _canvas; Opacity = 0; Width = 1; Height = 1;
            for (int i = 0; i < MaxSlots; i++)
            {
                var root = new Grid { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
                var border = new Border { BorderThickness = new Thickness(2.0), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, SnapsToDevicePixels = true };
                var tint = new Border { Margin = new Thickness(3), CornerRadius = new CornerRadius(5), Background = Brushes.Transparent };
                var sheen = new Border { Width = 12, HorizontalAlignment = HorizontalAlignment.Left, Background = CreateSheenBrush(), Opacity = 0 };
                // RarityBadgeSize (44) rather than the original 26: the source sparkle art is
                // thin-stroke line work covering only 10-16% of its canvas (Important/Optional),
                // so at 26px the strokes fall below one pixel and average out to near-transparent
                // grey - confirmed invisible in-game. The black drop shadow separates the badge
                // from the busy shop art behind it.
                var rarityIcon = new Image
                {
                    Width = RarityBadgeSize, Height = RarityBadgeSize, Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, RarityBadgeInsetY, RarityBadgeInsetX, 0),
                    IsHitTestVisible = false
                };
                RenderOptions.SetBitmapScalingMode(rarityIcon, BitmapScalingMode.HighQuality);
                Canvas.SetZIndex(rarityIcon, 5);
                root.Children.Add(border); root.Children.Add(tint); root.Children.Add(sheen); root.Children.Add(rarityIcon);
                var glow = new DropShadowEffect { BlurRadius = 0, ShadowDepth = 0, Opacity = 0.0, Direction = 0 };
                // Only attach the effect when the frame is actually drawn. A WPF Effect forces the
                // whole subtree through an offscreen render pass on every redraw - including the
                // badge's 12 frames per second - and it does that even at BlurRadius 0 and
                // Opacity 0. With the frame suppressed there is nothing for it to render, so
                // leaving it attached was pure cost.
                if (ShowHighlightFrame) root.Effect = glow;
                Canvas.SetZIndex(root, 500);
                _boxes.Add(root); _glows.Add(glow); _canvas.Children.Add(root);

                var debugBorder = new Border { BorderThickness = new Thickness(1.5), BorderBrush = Brushes.Lime, Background = Brushes.Transparent, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
                Canvas.SetZIndex(debugBorder, 600);
                _debugBoxes.Add(debugBorder); _canvas.Children.Add(debugBorder);
                var debugLabel = new TextBlock { Text = i.ToString(), Foreground = Brushes.Lime, Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), FontSize = 11, FontWeight = FontWeights.Bold, Padding = new Thickness(2, 0, 2, 0), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
                Canvas.SetZIndex(debugLabel, 601);
                _debugLabels.Add(debugLabel); _canvas.Children.Add(debugLabel);
            }
            Loaded += delegate { ApplyClickThrough(); RegisterHotkey(); };
            Closed += delegate { UnregisterHotkey(); };
        }

        public void RefreshNow() { Dispatcher.BeginInvoke(new Action(delegate { UpdateFromGame(HDTCore.Game); }), DispatcherPriority.Background); }

        public void UpdateFromGame(object game)
        {
            if (game == null || !PluginReflection.GetBool(game, "IsBattlegroundsMatch"))
            {
                HideAll();
                return;
            }
            // The shop-slot geometry in BuildSlots only means anything while the shop is open.
            // During combat the same screen area shows the fight board instead, so a stale/leftover
            // highlight box would land on whatever minion happens to occupy that position.
            if (PluginReflection.GetBool(game, "IsBattlegroundsCombatPhase"))
            {
                HideAll();
                return;
            }
            (int left, int top, int width, int height) rect; IntPtr handle;
            if (!TryFindHearthstone(out rect, out handle))
            {
                HideAll();
                return;
            }
            // Never keep shop highlights alive while another application/window is foreground.
            if (!Native.IsForegroundHearthstone())
            {
                HideAll();
                return;
            }

            if (!_hasStableRect || Math.Abs(rect.left - _stableRect.left) > 2 || Math.Abs(rect.top - _stableRect.top) > 2 ||
                Math.Abs(rect.width - _stableRect.width) > 2 || Math.Abs(rect.height - _stableRect.height) > 2)
            {
                _stableRect = rect;
                _hasStableRect = true;
            }
            rect = _stableRect;

            // Shop detection: read HDT's own Battlegrounds minion-pinning view model
            // (Core.Overlay.BattlegroundsMinionPinningViewModel.ShopCards) instead of guessing at
            // HDT's Entity/Zone model ourselves. That view model is what HDT's first-party "pin a
            // shop card" feature is built on, so it is already correctly kept in sync with the live
            // shop by HDT itself. The previous GuessShopEntities() heuristic required Zone==PLAY(1)
            // together with !IsInPlay, which is self-contradictory (IsInPlay simply means Zone==PLAY)
            // and so it could never return a single candidate - confirmed live via diagnostic logging.
            var pinningVm = HDTCore.Overlay != null ? HDTCore.Overlay.BattlegroundsMinionPinningViewModel : null;
            var shopCardIds = new List<string>();
            if (pinningVm != null && pinningVm.ShopCards != null)
            {
                foreach (var c in pinningVm.ShopCards)
                    if (c != null && c.IsSlotOccupied && !string.IsNullOrWhiteSpace(c.CardId))
                        shopCardIds.Add(c.CardId);
            }
            PlaceOver(rect.left, rect.top, rect.width, rect.height);

            List<string> live = shopCardIds.Take(MaxSlots).ToList();
            if (live.Count == 0)
            {
                HideAll();
                return;
            }

            var slots = BuildSlots(rect.width, rect.height, live.Count);

            if (_debugSlotsEnabled)
            {
                for (int i = 0; i < MaxSlots; i++)
                {
                    if (i >= live.Count) { _debugBoxes[i].Visibility = Visibility.Collapsed; _debugLabels[i].Visibility = Visibility.Collapsed; continue; }
                    PositionBox(_debugBoxes[i], slots[i], rect.width, rect.height);
                    Canvas.SetLeft(_debugLabels[i], Canvas.GetLeft(_debugBoxes[i]));
                    Canvas.SetTop(_debugLabels[i], Canvas.GetTop(_debugBoxes[i]) - 16);
                    _debugBoxes[i].Visibility = Visibility.Visible;
                    _debugLabels[i].Visibility = Visibility.Visible;
                }
            }
            else if (_debugBoxes[0].Visibility != Visibility.Collapsed)
            {
                for (int i = 0; i < MaxSlots; i++) { _debugBoxes[i].Visibility = Visibility.Collapsed; _debugLabels[i].Visibility = Visibility.Collapsed; }
            }

            // Rebuild the visible slot mapping from the live shop every tick.
            // This deliberately binds each highlight to the current slot content rather than
            // retaining a highlight by its previous visual slot. That fixes both purchased-card ghosts
            // and cases where Hearthstone re-centers the remaining cards after a purchase.
            for (int slotIndex = 0; slotIndex < live.Count; slotIndex++)
            {
                string cardId = live[slotIndex];

                bool highlight = IsExactActiveTarget(cardId);

                if (!highlight)
                {
                    _boxes[slotIndex].Visibility = Visibility.Collapsed;
                    _slotVisualState[slotIndex] = string.Empty;
                    _slotEntityKey[slotIndex] = string.Empty;
                    continue;
                }

                int cardTier = GetCardTierFromDb(cardId);
                int priority = _store.GetActivePriority(cardId);
                if (priority <= 0) priority = 1;

                string visualState = string.Concat(cardId, "|", slotIndex, "|", cardTier, "|", priority);
                if (!string.Equals(_slotVisualState[slotIndex], visualState, StringComparison.Ordinal))
                {
                    _slotVisualState[slotIndex] = visualState;
                    _slotEntityKey[slotIndex] = cardId;
                    // Explicit active-comp highlight: keep the target visible even when no board-match
                    // intelligence is available. Slot content still binds it to the live card.
                    StyleBox(_boxes[slotIndex], _glows[slotIndex], cardTier, priority, true, 1);
                }

                PositionBox(_boxes[slotIndex], slots[slotIndex], rect.width, rect.height);
                _boxes[slotIndex].Visibility = Visibility.Visible;
            }

            // Any old highlight with no corresponding live shop slot is immediately retired.
            for (int i = live.Count; i < MaxSlots; i++)
            {
                if (_boxes[i].Visibility == Visibility.Collapsed && _slotVisualState[i].Length == 0) continue;
                _boxes[i].Visibility = Visibility.Collapsed;
                StopRarityAnimation(_boxes[i]);
                _slotVisualState[i] = string.Empty;
                _slotEntityKey[i] = string.Empty;
            }
        }

        private static int GetCardTierFromDb(string cardId)
        {
            try
            {
                HearthDb.Card card;
                if (!string.IsNullOrWhiteSpace(cardId) && HearthDb.Cards.All != null &&
                    HearthDb.Cards.All.TryGetValue(cardId, out card) && card != null)
                    return card.Entity.GetTag(GameTag.TECH_LEVEL);
            }
            catch { }
            return 0;
        }

        private bool IsExactActiveTarget(string cardId)
        {
            if (string.IsNullOrWhiteSpace(cardId)) return false;
            // Shop targeting intentionally uses the exact live CardId.
            // Golden normalization is unnecessary: the live Battlegrounds shop does not surface
            // golden variants as ordinary shop candidates, so expanding the matcher would only
            // create false positives.
            return _store.ActiveIds.Contains(cardId.Trim(), StringComparer.OrdinalIgnoreCase);
        }

        private static void StyleBox(Grid root, DropShadowEffect glow, int tier, int priority, bool boardMatch, int ownedCopies)
        {
            Border border = root.Children[0] as Border;
            Border tint = root.Children[1] as Border;
            Border sheen = root.Children[2] as Border;
            Image rarityIcon = root.Children.Count > 3 ? root.Children[3] as Image : null;
            if (rarityIcon != null)
            {
                rarityIcon.BeginAnimation(Image.SourceProperty, null);
                rarityIcon.BeginAnimation(Image.SourceProperty, GetRarityAnimation(priority));
            }

            // Badge-only presentation: the priority frame is suppressed, leaving just the sparkle
            // over the card. All the frame styling below is left intact so this is a one-flag
            // revert rather than a deletion.
            if (!ShowHighlightFrame)
            {
                border.Visibility = Visibility.Collapsed;
                tint.Visibility = Visibility.Collapsed;
                sheen.BeginAnimation(Canvas.LeftProperty, null);
                sheen.Opacity = 0;
                sheen.Visibility = Visibility.Collapsed;
                glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
                glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
                glow.Opacity = 0.0;
                glow.BlurRadius = 0;
                return;
            }

            // Fully static rendering: no Storyboards, no pulsing, no transform animation.
            glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            sheen.BeginAnimation(Canvas.LeftProperty, null);
            sheen.Opacity = 0;

            bool isCore = priority == 1;
            bool isImportant = priority == 2;
            bool isDoubleBoardMatch = boardMatch && ownedCopies >= 2;
            Color tierColor = GetCardTierColor(tier);

            // Always render the user's priority frame. A board-match gets an inner wind/sheen
            // treatment, but never replaces Core/Important/Optional with a generic white frame.
            if (isCore)
            {
                border.BorderThickness = new Thickness(boardMatch ? 2.25 : 2.5);
                border.BorderBrush = CreateCoreBorderBrush();
                tint.BorderThickness = boardMatch ? new Thickness(1.0) : new Thickness(1.25);
                tint.BorderBrush = new SolidColorBrush(Color.FromRgb(156, 42, 128));
                glow.Color = Color.FromRgb(196, 52, 168);
            }
            else if (isImportant)
            {
                border.BorderThickness = new Thickness(boardMatch ? 2.25 : 2.5);
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 137, 58));
                tint.BorderThickness = boardMatch ? new Thickness(1.0) : new Thickness(1.25);
                tint.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 205, 90));
                glow.Color = Color.FromRgb(255, 137, 58);
            }
            else
            {
                border.BorderThickness = new Thickness(boardMatch ? 2.0 : 2.25);
                border.BorderBrush = new SolidColorBrush(tierColor);
                tint.BorderThickness = boardMatch ? new Thickness(0.8) : new Thickness(1.0);
                tint.BorderBrush = new SolidColorBrush(Color.FromArgb(155, tierColor.R, tierColor.G, tierColor.B));
                glow.Color = tierColor;
            }

            tint.Background = Brushes.Transparent;
            border.Background = Brushes.Transparent;
            sheen.Margin = new Thickness(0);
            sheen.Width = 12;
            sheen.HorizontalAlignment = HorizontalAlignment.Left;

            if (boardMatch)
            {
                // The match effect is clipped by the card target itself. It is deliberately static
                // in this beta so it cannot move the geometry of the targeting frame.
                tint.Background = CreateBoardMatchWindBrush(isDoubleBoardMatch ? 0.26 : 0.18,
                    isDoubleBoardMatch ? 0.78 : 0.56);
                sheen.Background = CreateSheenBrush(isDoubleBoardMatch ? Color.FromRgb(180,250,255) : Color.FromRgb(120,245,255));
                sheen.Margin = new Thickness(5);
                sheen.Width = 18;
                sheen.HorizontalAlignment = HorizontalAlignment.Left;
                sheen.Opacity = isDoubleBoardMatch ? 0.38 : 0.22;
            }

            glow.BlurRadius = boardMatch ? 14 : 0;
            glow.ShadowDepth = 0;
            if (boardMatch)
            {
                glow.Opacity = 0.46;
                var pulse = new DoubleAnimation(0.36, 0.54, TimeSpan.FromMilliseconds(850))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                };
                glow.BeginAnimation(DropShadowEffect.OpacityProperty, pulse);
            }
            else
            {
                glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
                glow.Opacity = 0.0;
            }
        }

        // Rarity-glow badge: a looping sparkle shown on a highlighted card, coloured by priority
        // (Core/Important/Optional). The sheets are 24 evenly-timed frames cut from one 6x4 grid
        // per tier, so the animation is a plain uniform frame rate over RarityBadgeLoopSeconds -
        // no per-frame timestamp table.
        private const double RarityBadgeLoopSeconds = 2.0;
        private static readonly Dictionary<int, ObjectAnimationUsingKeyFrames> RarityAnimationCache = new Dictionary<int, ObjectAnimationUsingKeyFrames>();
        private static readonly object RarityAnimationCacheLock = new object();

        // On-screen size of the badge, and the alpha curve applied to its frames. The current
        // sheets ship a real alpha channel with bold, thick shapes, so the gamma lift is mild -
        // it only deepens contrast slightly. (The previous art needed a strong lift because it
        // was thin line work keyed out of a glow-on-black export.)
        private const int RarityBadgeSize = 44;
        private const double RarityBadgeAlphaGamma = 1.0;
        // Radius (as a fraction of the inscribed circle) where the badge's radial falloff starts.
        // The extractor already cuts each frame on its detected sprite band and deletes anything
        // connected to the window border, so bleed from neighbouring frames is gone before the
        // art ships. Every tier's own content stops around r = 0.92-0.93, so this only trims
        // outside all of it. Do NOT lower it much: at 0.70 it visibly clips the star's ray tips.
        private const double RarityBadgeVignetteStart = 0.95;

        // Badge position, measured inwards from the highlight box's top-right corner. It used to
        // hang half outside the card (negative margins), which read as badly placed - it now sits
        // inside the card art. For a badge centred on the card instead, switch the Image's
        // alignments to Center and set both insets to 0.
        private const double RarityBadgeInsetX = 4.0;
        private const double RarityBadgeInsetY = 4.0;

        // Whether to draw the coloured priority frame around a highlighted card. Off: the sparkle
        // badge alone marks the target. Flip to true to bring the whole frame treatment back.
        private const bool ShowHighlightFrame = false;

        // Crops a frame down to its actual painted area and lifts its alpha. Important/Optional
        // frames only fill ~10-16% of their source canvas, so cropping alone makes the visible
        // sparkle substantially bigger at the same on-screen box.
        private static BitmapSource LoadBadgeFrame(string path)
        {
            try
            {
                var src = new BitmapImage();
                src.BeginInit();
                src.CacheOption = BitmapCacheOption.OnLoad;
                src.UriSource = new Uri(path, UriKind.Absolute);
                src.EndInit();

                // Straight (non-premultiplied) alpha, so raising alpha does not wash out the colour.
                var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0.0);
                int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
                var px = new byte[stride * h];
                bgra.CopyPixels(px, stride, 0);

                for (int i = 3; i < px.Length; i += 4)
                {
                    int a = px[i];
                    if (a > 0) px[i] = (byte)Math.Min(255.0, Math.Round(255.0 * Math.Pow(a / 255.0, RarityBadgeAlphaGamma)));
                }

                // The Core sheet was exported with the sparkle already running off its canvas
                // (142 of 150 bottom-edge pixels opaque, 104 left, 121 right), so it ends in a hard
                // straight cut that reads in game as a dark rectangle around the badge. Those
                // pixels cannot be recovered, but a radial falloff retires the corners entirely,
                // so what is left fades as a round glow instead of a cropped box. A plain
                // border-band feather was tried first and was not enough - the Core art is a
                // nearly square glow field, so only a radial mask removes the rectangle.
                double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0, rr = Math.Min(w, h) / 2.0;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int o = y * stride + x * 4 + 3;
                        if (px[o] == 0) continue;
                        double dx = x - cx, dy = y - cy;
                        double r = Math.Sqrt(dx * dx + dy * dy) / rr;
                        if (r <= RarityBadgeVignetteStart) continue;
                        if (r >= 1.0) { px[o] = 0; continue; }
                        double t = (r - RarityBadgeVignetteStart) / (1.0 - RarityBadgeVignetteStart);
                        px[o] = (byte)(px[o] * (1.0 - t * t * (3.0 - 2.0 * t)));
                    }
                }

                // Bounding box is measured after the mask, so the crop tracks what actually
                // survives it and the sparkle fills more of its on-screen box.
                int x0 = w, y0 = h, x1 = -1, y1 = -1;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        if (px[y * stride + x * 4 + 3] <= 8) continue;
                        if (x < x0) x0 = x;
                        if (x > x1) x1 = x;
                        if (y < y0) y0 = y;
                        if (y > y1) y1 = y;
                    }
                }

                BitmapSource boosted = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
                if (x1 < x0 || y1 < y0)
                {
                    if (boosted.CanFreeze) boosted.Freeze();
                    return boosted;
                }
                var cropped = new CroppedBitmap(boosted, new Int32Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1));
                if (cropped.CanFreeze) cropped.Freeze();
                return cropped;
            }
            catch { return null; }
        }

        private static ObjectAnimationUsingKeyFrames GetRarityAnimation(int priority)
        {
            lock (RarityAnimationCacheLock)
            {
                ObjectAnimationUsingKeyFrames cached;
                if (RarityAnimationCache.TryGetValue(priority, out cached)) return cached;

                string folder = priority == 1 ? "Core" : priority == 2 ? "Important" : "Optional";
                string assemblyDir = IOPath.GetDirectoryName(typeof(ShopWishlistPlugin).Assembly.Location) ?? string.Empty;
                string dir = IOPath.Combine(assemblyDir, "Assets", "RarityGlow", folder);

                var anim = new ObjectAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(RarityBadgeLoopSeconds), RepeatBehavior = RepeatBehavior.Forever };
                try
                {
                    string[] files = Directory.Exists(dir) ? Directory.GetFiles(dir, "frame_*.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray() : new string[0];
                    double step = files.Length > 0 ? RarityBadgeLoopSeconds / files.Length : 0.0;
                    for (int i = 0; i < files.Length; i++)
                    {
                        BitmapSource bmp = LoadBadgeFrame(files[i]);
                        if (bmp == null) continue;
                        anim.KeyFrames.Add(new DiscreteObjectKeyFrame(bmp, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(i * step))));
                    }
                }
                catch { }
                anim.Freeze();
                RarityAnimationCache[priority] = anim;
                return anim;
            }
        }

        private static Brush CreateCoreBorderBrush()
        {
            var g = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };
            g.GradientStops.Add(new GradientStop(Color.FromRgb(105, 40, 195), 0.0));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(165, 70, 245), 0.44));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(255, 214, 75), 0.50));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(255, 232, 130), 0.55));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(210, 155, 40), 1.0));
            return g;
        }

        private static Brush CreateBoardMatchWindBrush(double alpha, double centerAlpha)
        {
            byte a0 = (byte)Math.Max(0, Math.Min(255, (int)(255 * alpha)));
            byte ac = (byte)Math.Max(0, Math.Min(255, (int)(255 * centerAlpha)));
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            Color c = Color.FromRgb(90, 235, 255);
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0.0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(a0, c.R, c.G, c.B), 0.32));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(ac, 235, 255, 255), 0.48));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(a0, c.R, c.G, c.B), 0.62));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1.0));
            return g;
        }

        private static Brush CreateSheenBrush()
        {
            return CreateSheenBrush(Color.FromRgb(255,255,255));
        }

        private static Brush CreateSheenBrush(Color tintColor)
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0,0), EndPoint = new Point(1,0) };
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0,tintColor.R,tintColor.G,tintColor.B),0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(150,tintColor.R,tintColor.G,tintColor.B),0.5));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0,tintColor.R,tintColor.G,tintColor.B),1));
            return g;
        }

        private static Brush CreateBorderBrush(Color c, int priority)
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0,0), EndPoint = new Point(1,1) };
            byte a = (byte)Math.Min(255, 190 + priority * 20);
            g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Max(120, a - 50), c.R,c.G,c.B),0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(255,c.R,c.G,c.B),0.5));
            g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Max(140, a - 20), c.R,c.G,c.B),1));
            return g;
        }

        private static Brush CreateSplitBorderBrush(Color first, Color second)
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            g.GradientStops.Add(new GradientStop(first, 0));
            g.GradientStops.Add(new GradientStop(first, 0.42));
            g.GradientStops.Add(new GradientStop(second, 0.58));
            g.GradientStops.Add(new GradientStop(second, 1));
            return g;
        }

        private static Color GetCardTierColor(int tier)
        {
            // High-contrast neon palette chosen to remain legible against Hearthstone's animated board.
            if (tier <= 0) return Color.FromRgb(175, 95, 255); // deep-violet warm-up fallback
            if (tier <= 3) return Color.FromRgb(0, 235, 155);   // electric emerald
            if (tier == 4) return Color.FromRgb(255, 205, 65);  // bright gold
            if (tier == 5) return Color.FromRgb(255, 105, 65);  // coral-orange
            return Color.FromRgb(255, 55, 190);                // vivid magenta for T6+
        }

        private static (double x, double y, double w, double h)[] BuildSlots(double width, double height, int count)
        {
            count = Math.Max(1, Math.Min(MaxSlots, count));
            double aspect = height <= 0 ? 1.77 : width / height;
            // The X calibration was already correct in the field. The recurring visual defect was vertical:
            // the top edge was close to the real shop card, while the box extended too far below it.
            // Keep the existing normalized shop Y and width, but use a card-height ratio closer to the
            // actual Battlegrounds shop card bounds. HDT itself scales overlay geometry from the live
            // Hearthstone window rect and a 1080px baseline, so normalized coordinates remain stable
            // across supported resolutions/aspect ratios.
            // Card pitch (center-to-center spacing) was already correct; only the box WIDTH was too
            // wide, spilling into the gaps between cards. Narrow cardW but grow gap by the same
            // amount so cardW+gap (the pitch) is unchanged - that keeps every card's center exactly
            // where it was while the box now hugs the card's left/right edges instead of overshooting.
            double cardW = aspect > 2.0 ? 0.0525 : 0.054;
            double gap = 0.017;
            double y = 0.307;
            // Calibrated against the real Battlegrounds shop card body at the current HDT overlay scale.
            // Keep the top edge anchored and reduce the box height so the bottom no longer hangs below the card.
            double h = 0.138;
            // Fine nudge from live screenshot feedback: the frame sat slightly high of the actual card
            // body, and needed a touch more downward correction on top of the first pass.
            double nudgeX = -0.004;
            double nudgeY = 0.014;
            double total = count * cardW + Math.Max(0, count - 1) * gap;
            double start = (1.0 - total) / 2.0 + nudgeX;
            var slots = new (double x,double y,double w,double h)[count];
            for (int i=0;i<count;i++) slots[i] = (start + i*(cardW+gap), y + nudgeY, cardW, h);
            return slots;
        }

        private static void PositionBox(FrameworkElement box, (double x,double y,double w,double h) slot, double width, double height)
        {
            double left = Math.Round(slot.x * width);
            double top = Math.Round(slot.y * height);
            double boxWidth = Math.Round(slot.w * width);
            double boxHeight = Math.Round(slot.h * height);
            Canvas.SetLeft(box, left);
            Canvas.SetTop(box, top);
            box.Width = Math.Max(1, boxWidth);
            box.Height = Math.Max(1, boxHeight);
            // Make WPF rasterization deterministic for a pixel-aligned overlay.
            box.SnapsToDevicePixels = true;
            box.UseLayoutRounding = true;
        }

        private void PlaceOver(int left,int top,int width,int height)
        {
            Opacity = 1;
            Left = left;
            Top = top;
            Width = Math.Max(1.0, (double)width);
            Height = Math.Max(1.0, (double)height);
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            _canvas.Width = Width;
            _canvas.Height = Height;
        }
        // Stops a slot's badge loop. A RepeatBehavior.Forever animation keeps its clock running
        // and keeps writing the property even when the element is collapsed, so a badge styled
        // once went on costing ~12 property writes per second per slot for as long as HDT stayed
        // open - main menu included. _slotVisualState is cleared alongside, so StyleBox
        // reinstates the animation when the slot comes back.
        private static void StopRarityAnimation(Grid root)
        {
            try
            {
                Image icon = root != null && root.Children.Count > 3 ? root.Children[3] as Image : null;
                if (icon == null) return;
                icon.BeginAnimation(Image.SourceProperty, null);
                icon.Source = null;
            }
            catch { }
        }

        private void HideAll(){ for(int i=0;i<_boxes.Count;i++){ _boxes[i].Visibility=Visibility.Collapsed; StopRarityAnimation(_boxes[i]); _slotVisualState[i]=string.Empty; _slotEntityKey[i]=string.Empty; } for(int i=0;i<_debugBoxes.Count;i++){ _debugBoxes[i].Visibility=Visibility.Collapsed; _debugLabels[i].Visibility=Visibility.Collapsed; } Opacity=0; }
        public void HideForExternalFocus(){ HideAll(); }
        private void ApplyClickThrough(){ HwndSource s=PresentationSource.FromVisual(this) as HwndSource; if(s==null)return; IntPtr h=s.Handle; int ex=Native.GetWindowLong(h,Native.GWL_EXSTYLE); Native.SetWindowLong(h,Native.GWL_EXSTYLE,ex|Native.WS_EX_TRANSPARENT|Native.WS_EX_LAYERED|Native.WS_EX_TOOLWINDOW|Native.WS_EX_NOACTIVATE); }
        private void RegisterHotkey()
        {
            try
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                if (h != IntPtr.Zero)
                {
                    Native.RegisterHotKey(h, 1338, Native.MOD_CONTROL | Native.MOD_SHIFT, (uint)KeyInterop.VirtualKeyFromKey(Key.W));
                    // Manual troubleshooting hotkey: drop and reconnect the native Battlegrounds
                    // memory binding (tavern tier/faction rail) if it ever gets stuck.
                    Native.RegisterHotKey(h, 1339, Native.MOD_CONTROL | Native.MOD_SHIFT, (uint)KeyInterop.VirtualKeyFromKey(Key.R));
                    // Calibration aid: toggle debug outlines on every shop slot (see _debugSlotsEnabled).
                    Native.RegisterHotKey(h, 1340, Native.MOD_CONTROL | Native.MOD_SHIFT, (uint)KeyInterop.VirtualKeyFromKey(Key.G));
                    // Investigation aid: dump Mono classes matching Tavern/Shop/Bacon to a log file.
                    Native.RegisterHotKey(h, 1341, Native.MOD_CONTROL | Native.MOD_SHIFT, (uint)KeyInterop.VirtualKeyFromKey(Key.M));
                    // Investigation aid: dump TB_BaconShop's field names/types to a log file.
                    Native.RegisterHotKey(h, 1342, Native.MOD_CONTROL | Native.MOD_SHIFT, (uint)KeyInterop.VirtualKeyFromKey(Key.F));
                }
                HwndSource src = HwndSource.FromHwnd(h);
                if (src != null) src.AddHook(WndProc);
            }
            catch { }
        }

        private void UnregisterHotkey()
        {
            try { Native.UnregisterHotKey(new WindowInteropHelper(this).Handle, 1338); } catch { }
            try { Native.UnregisterHotKey(new WindowInteropHelper(this).Handle, 1339); } catch { }
            try { Native.UnregisterHotKey(new WindowInteropHelper(this).Handle, 1340); } catch { }
            try { Native.UnregisterHotKey(new WindowInteropHelper(this).Handle, 1341); } catch { }
            try { Native.UnregisterHotKey(new WindowInteropHelper(this).Handle, 1342); } catch { }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && wParam.ToInt32() == 1338)
            {
                handled = true;
                if (_toggleSettings != null) Dispatcher.BeginInvoke(_toggleSettings, DispatcherPriority.Background);
            }
            else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == 1339)
            {
                handled = true;
                try { BattlegroundsScryMemory.Instance.Disconnect(); } catch { }
            }
            else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == 1340)
            {
                handled = true;
                _debugSlotsEnabled = !_debugSlotsEnabled;
                if (!_debugSlotsEnabled) for (int i = 0; i < _debugBoxes.Count; i++) { _debugBoxes[i].Visibility = Visibility.Collapsed; _debugLabels[i].Visibility = Visibility.Collapsed; }
            }
            else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == 1341)
            {
                handled = true;
                string logPath = IOPath.Combine(IOPath.GetTempPath(), "hdt_scry_classscan.log");
                Task.Run(delegate { BattlegroundsScryMemory.Instance.DumpMatchingClassesToFile(logPath, "Tavern", "Shop", "Bacon"); });
            }
            else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == 1342)
            {
                handled = true;
                string logPath = IOPath.Combine(IOPath.GetTempPath(), "hdt_scry_fields.log");
                Task.Run(delegate { BattlegroundsScryMemory.Instance.DumpClassFieldNamesToFile(logPath, "TB_BaconShop"); });
            }
            return IntPtr.Zero;
        }

        private static bool TryFindHearthstone(out (int left,int top,int width,int height) rect,out IntPtr handle)
        {
            Native.RECT r;
            if (Native.TryFindHearthstoneWindow(out r, out handle)) { rect = (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top); return true; }
            rect = default((int, int, int, int)); return false;
        }
    }

    internal sealed class CardDescriptor
    {
        public string Id;
        public string Name;
        public List<string> Tribes = new List<string>();
        public int Tier;
        public ImageSource Image;
        public string ImageUrl;
        public string Category;
        public override string ToString() { return Name; }
        public bool HasTribe(string tribe)
        {
            if (string.IsNullOrWhiteSpace(tribe) || string.Equals(tribe, "All Tribes", StringComparison.OrdinalIgnoreCase)) return true;
            string wanted = NormalizeForFilter(tribe);
            return Tribes.Any(t => string.Equals(NormalizeForFilter(t), wanted, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeForFilter(string value)
        {
            string s = value ?? string.Empty;
            if (s.StartsWith("RACE_", StringComparison.OrdinalIgnoreCase)) s = s.Substring(5);
            s = s.Replace("_", string.Empty).Replace("-", string.Empty).Trim();
            if (s.Equals("QUILBOAR", StringComparison.OrdinalIgnoreCase)) return "QUILBOAR";
            return s;
        }
        public string TribeLabel { get { return Tribes.Count == 0 ? "Neutral" : string.Join(" / ", Tribes); } }
    }

    internal sealed class CurrentPoolCard
    {
        public string Id;
        public string Name;
        public int Tier;
        public List<string> Tribes = new List<string>();
        public string ImageUrl;
        public string Category;
        public bool IsTimewarped;
    }

    internal static class CurrentPoolLoader
    {
        private const int CardTypeTavernSpell = 42;
        private const int SpellSchoolTavern = 9;
        private const string RemoteImageBase = "https://hsbg.cards/api/v1/cards/";

        public static List<CurrentPoolCard> Load(out string status)
        {
            var all = new List<CurrentPoolCard>();
            try
            {
                BuildLocalCurrentPool(all);
                all = all.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                         .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                         .Select(g => g.First())
                         .OrderBy(x => x.Category).ThenBy(x => x.Tier).ThenBy(x => x.Name)
                         .ToList();
                int minions = all.Count(x => string.Equals(x.Category,"Minions",StringComparison.OrdinalIgnoreCase));
                int spells = all.Count(x => string.Equals(x.Category,"Tavern Spells",StringComparison.OrdinalIgnoreCase));
                int buddies = all.Count(x => string.Equals(x.Category,"Buddies",StringComparison.OrdinalIgnoreCase));
                status = "Live local HDT pool: " + all.Count + " cards (" + minions + " minions, " + spells + " Tavern Spells, " + buddies + " Buddies).";
                return all;
            }
            catch(Exception ex)
            {
                Debug.WriteLine("Local current pool failed: " + ex);
                status = "Local HDT pool failed: " + ex.Message;
                return new List<CurrentPoolCard>();
            }
        }

        private static void BuildLocalCurrentPool(List<CurrentPoolCard> all)
        {
            if (Cards.All == null) throw new InvalidOperationException("HearthDb.Cards.All is unavailable.");

            var currentBuddyDbfs = new HashSet<int>();
            foreach (var kv in Cards.All)
            {
                HearthDb.Card hero = kv.Value;
                if (GetTag(hero, GameTag.BACON_HERO_CAN_BE_DRAFTED) != 1) continue;
                int companionDbf = GetTag(hero, GameTag.BACON_COMPANION_ID);
                if (companionDbf > 0) currentBuddyDbfs.Add(companionDbf);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in Cards.All)
            {
                string id = kv.Key;
                HearthDb.Card card = kv.Value;
                if (string.IsNullOrWhiteSpace(id) || card == null || !IsBattlegroundsId(id)) continue;
                if (ShopWishlistPlugin.IsGoldenBattlegroundsVariant(id)) continue;
                if (IsTimewarpedName(card.Name)) continue;
                if (GetTag(card, GameTag.BACON_OMIT_WHEN_OUT_OF_ROTATION) == 1) continue;

                // Current Battlegrounds minion pool. This is the authoritative local tag; no remote pool is needed.
                if (GetTag(card, GameTag.IS_BACON_POOL_MINION) == 1)
                {
                    int tier = GetTag(card, GameTag.TECH_LEVEL);
                    if (tier > 0 && seen.Add(id)) all.Add(CreateCard(card, id, "Minions", tier));
                    continue;
                }

                int cardType = GetTag(card, GameTag.CARDTYPE);
                int tierForSpell = GetTag(card, GameTag.TECH_LEVEL);
                int spellSchool = GetTag(card, GameTag.SPELL_SCHOOL);
                int actionCard = GetTag(card, GameTag.BACON_ACTION_CARD);

                // Tavern spells are a real card type + Tavern spell school/action tag.
                // This avoids accidentally classifying minions that merely mention the words "Tavern spell".
                if (cardType == 42 && tierForSpell > 0 && (spellSchool == 9 || actionCard == 1))
                {
                    if (seen.Add(id)) all.Add(CreateCard(card, id, "Tavern Spells", tierForSpell));
                    continue;
                }

                // Current Buddies: prefer companions linked from currently draftable heroes.
                int dbfId = GetDbfId(card);
                if (GetTag(card, GameTag.BACON_BUDDY) == 1 && GetTag(card, GameTag.BACON_OMIT_WHEN_OUT_OF_ROTATION) != 1)
                {
                    bool linkedToCurrentHero = currentBuddyDbfs.Count == 0 || currentBuddyDbfs.Contains(dbfId);
                    int tier = GetTag(card, GameTag.TECH_LEVEL);
                    if (linkedToCurrentHero && tier > 0 && seen.Add(id)) all.Add(CreateCard(card, id, "Buddies", tier));
                }
            }

            // Defensive fallback: if the hero-to-buddy links are not present in a particular CardDefs build,
            // include non-omitted BACON_BUDDY cards from the same local CardDefs rather than returning zero buddies.
            if (!all.Any(x => string.Equals(x.Category, "Buddies", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var kv in Cards.All)
                {
                    string id = kv.Key;
                    HearthDb.Card card = kv.Value;
                    if (string.IsNullOrWhiteSpace(id) || card == null || !IsBattlegroundsId(id) || ShopWishlistPlugin.IsGoldenBattlegroundsVariant(id) || IsTimewarpedName(card.Name)) continue;
                    if (GetTag(card, GameTag.BACON_OMIT_WHEN_OUT_OF_ROTATION) == 1 || GetTag(card, GameTag.BACON_BUDDY) != 1) continue;
                    int tier = GetTag(card, GameTag.TECH_LEVEL);
                    if (tier > 0 && seen.Add(id)) all.Add(CreateCard(card, id, "Buddies", tier));
                }
            }
        }

        private static bool IsTimewarpedName(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && name.IndexOf("timewarped", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static CurrentPoolCard CreateCard(HearthDb.Card card,string id,string category,int tier)
        {
            return new CurrentPoolCard
            {
                Id=id,
                Name=string.IsNullOrWhiteSpace(card.Name)?id:card.Name,
                Tier=tier,
                Tribes=GetLocalTribes(card),
                ImageUrl=RemoteImageBase+Uri.EscapeDataString(id)+"/image?size=full&format=png",
                Category=category,
                IsTimewarped=false
            };
        }

        private static List<string> GetLocalTribes(HearthDb.Card card)
        {
            var tribes = new List<string>();
            var candidates = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "Beast", new[] { "BACON_SUBSET_BEAST", "BACON_SUBSET_BEASTS" } },
                { "Demon", new[] { "BACON_SUBSET_DEMON", "BACON_SUBSET_DEMONS" } },
                { "Dragon", new[] { "BACON_SUBSET_DRAGON", "BACON_SUBSET_DRAGONS" } },
                { "Elemental", new[] { "BACON_SUBSET_ELEMENTAL", "BACON_SUBSET_ELEMENTALS" } },
                { "Mech", new[] { "BACON_SUBSET_MECH", "BACON_SUBSET_MECHS" } },
                { "Murloc", new[] { "BACON_SUBSET_MURLOC", "BACON_SUBSET_MURLOCS" } },
                { "Naga", new[] { "BACON_SUBSET_NAGA", "BACON_SUBSET_NAGAS" } },
                { "Pirate", new[] { "BACON_SUBSET_PIRATE", "BACON_SUBSET_PIRATES" } },
                { "Quilboar", new[] { "BACON_SUBSET_QUILBOAR", "BACON_SUBSET_QUILBOARS" } },
                { "Undead", new[] { "BACON_SUBSET_UNDEAD", "BACON_SUBSET_UNDEADS" } }
            };
            foreach (var pair in candidates)
            {
                foreach (string tagName in pair.Value)
                {
                    GameTag tag;
                    if (Enum.TryParse<GameTag>(tagName, true, out tag) && GetTag(card, tag) > 0)
                    {
                        tribes.Add(pair.Key);
                        break;
                    }
                }
            }
            if (tribes.Count == 0)
            {
                try
                {
                    string race = card.Race.ToString();
                    race = NormalizeTribe(race);
                    if (!string.IsNullOrWhiteSpace(race) && !race.Equals("Neutral", StringComparison.OrdinalIgnoreCase)) tribes.Add(race);
                }
                catch { }
            }
            if (tribes.Count == 0) tribes.Add("Neutral");
            return tribes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string NormalizeTribe(string race)
        {
            if(string.IsNullOrWhiteSpace(race)) return "Neutral";
            string s=race.Trim();
            if(s.Equals("INVALID",StringComparison.OrdinalIgnoreCase) || s.Equals("NONE",StringComparison.OrdinalIgnoreCase)) return "Neutral";
            if(s.Equals("MECHANICAL",StringComparison.OrdinalIgnoreCase) || s.Equals("MECH",StringComparison.OrdinalIgnoreCase)) return "Mech";
            if(s.Equals("QUILBOAR",StringComparison.OrdinalIgnoreCase)) return "Quilboar";
            if(s.Equals("MURLOC",StringComparison.OrdinalIgnoreCase)) return "Murloc";
            if(s.Equals("PIRATE",StringComparison.OrdinalIgnoreCase)) return "Pirate";
            if(s.Equals("DRAGON",StringComparison.OrdinalIgnoreCase)) return "Dragon";
            if(s.Equals("DEMON",StringComparison.OrdinalIgnoreCase)) return "Demon";
            if(s.Equals("BEAST",StringComparison.OrdinalIgnoreCase)) return "Beast";
            if(s.Equals("ELEMENTAL",StringComparison.OrdinalIgnoreCase)) return "Elemental";
            if(s.Equals("NAGA",StringComparison.OrdinalIgnoreCase)) return "Naga";
            if(s.Equals("UNDEAD",StringComparison.OrdinalIgnoreCase)) return "Undead";
            return s;
        }

        private static int GetTag(HearthDb.Card card, GameTag tag)
        {
            try { return card != null && card.Entity != null ? card.Entity.GetTag(tag) : 0; }
            catch { return 0; }
        }

        private static int GetDbfId(HearthDb.Card card)
        {
            if(card==null) return 0;
            try
            {
                PropertyInfo p=card.GetType().GetProperty("DbfId", BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                if(p!=null)
                {
                    object v=p.GetValue(card,null);
                    int parsed; if(v!=null && int.TryParse(Convert.ToString(v),out parsed)) return parsed;
                }
            } catch {}
            try { return card.Entity != null ? card.Entity.GetTag(GameTag.ENTITY_ID) : 0; } catch { return 0; }
        }

        private static bool IsBattlegroundsId(string id)
        {
            return !string.IsNullOrWhiteSpace(id) && (id.StartsWith("BG",StringComparison.OrdinalIgnoreCase) || id.StartsWith("BGS_",StringComparison.OrdinalIgnoreCase));
        }





        public static List<CurrentPoolCard> LoadBuddies(out string status)
        {
            var all = Load(out status);
            return all.Where(x => string.Equals(x.Category, "Buddies", StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    internal sealed class InGameLauncherWindow : Window
    {
        private readonly WishlistStore _store;
        private readonly Action _openBuilder;
        private Border _frame;
        private Image _image;
        private bool _dragging;
        private bool _dragMoved;
        private Point _dragStartScreen;
        private double _dragStartLeft;
        private double _dragStartTop;
        private Rect _lastKnownGameRect = Rect.Empty;

        public InGameLauncherWindow(WishlistStore store, Action openBuilder)
        {
            _store = store;
            _openBuilder = openBuilder;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Width = 58; Height = 58;
            Content = BuildContent();
            Loaded += delegate { ApplyLauncherStyle(); };
            MouseLeftButtonDown += LauncherMouseDown;
            MouseMove += LauncherMouseMove;
            MouseLeftButtonUp += LauncherMouseUp;
        }

        private UIElement BuildContent()
        {
            _frame = new Border
            {
                Width = 52, Height = 52,
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1.5),
                BorderBrush = new LinearGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(180, 105, 255), 0),
                    new GradientStop(Color.FromRgb(255, 205, 75), 1)
                }),
                Background = new SolidColorBrush(Color.FromArgb(90, 10, 8, 20)),
                Cursor = Cursors.Hand,
                ToolTip = "Open BG Comp Builder — Ctrl + Shift + W",
                Effect = new DropShadowEffect { Color = Color.FromRgb(40, 235, 255), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.55 }
            };
            _image = new Image { Width = 46, Height = 46, Stretch = Stretch.Uniform, Margin = new Thickness(3) };
            string assemblyDir = IOPath.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = IOPath.Combine(assemblyDir ?? AppDomain.CurrentDomain.BaseDirectory, "Assets", "BGCompBuilderIcon.png");
            try
            {
                if (File.Exists(path))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(path, UriKind.Absolute); bmp.EndInit(); bmp.Freeze(); _image.Source = bmp;
                }
            } catch { }
            _frame.Child = _image;
            return _frame;
        }

        public void UpdateForCurrentGame(object game)
        {
            bool show = game != null && PluginReflection.GetBool(game, "IsBattlegroundsMatch");
            Rect rect; IntPtr handle;
            if (show && TryFindHearthstone(out rect, out handle))
            {
                _lastKnownGameRect = rect;

                // Never fight the user's own drag: while dragging, LauncherMouseMove is already the
                // sole authority over Left/Top for this tick. Re-deriving position from the stored
                // (pre-drag) position here on every poll raced against live mouse movement and made
                // the icon visibly snap/jitter back toward its old spot while being dragged.
                if (_dragging)
                {
                    if (Visibility != Visibility.Visible) Show();
                    return;
                }

                Point stored = _store != null ? _store.LoadLauncherPosition() : new Point(0.028, 0.82);
                double maxX = Math.Max(8.0, rect.Width - Width - 8.0);
                double maxY = Math.Max(8.0, rect.Height - Height - 8.0);
                double localX = stored.X * Math.Max(1.0, rect.Width);
                double localY = stored.Y * Math.Max(1.0, rect.Height);

                bool stale = localX < 8.0 || localY < 8.0 || localX > maxX || localY > maxY;
                if (stale)
                {
                    localX = Math.Max(8.0, Math.Min(maxX, rect.Width * 0.028));
                    localY = Math.Max(8.0, Math.Min(maxY, rect.Height * 0.82));
                }
                else
                {
                    localX = Math.Max(8.0, Math.Min(maxX, localX));
                    localY = Math.Max(8.0, Math.Min(maxY, localY));
                }

                Left = rect.Left + localX;
                Top = rect.Top + localY;

                if (_store != null)
                {
                    double rx = localX / Math.Max(1.0, rect.Width);
                    double ry = localY / Math.Max(1.0, rect.Height);
                    _store.SaveLauncherPosition(new Point(
                        Math.Max(0.0, Math.Min(0.94, rx)),
                        Math.Max(0.0, Math.Min(0.94, ry))));
                }

                if (Visibility != Visibility.Visible) Show();
            }
            else if (!show)
            {
                if (Visibility != Visibility.Hidden) Hide();
            }
            else if (_lastKnownGameRect != Rect.Empty && Visibility != Visibility.Visible)
            {
                Show();
            }
        }

        private void LauncherMouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;
            _dragMoved = false;
            _dragStartScreen = PointToScreen(e.GetPosition(this));
            _dragStartLeft = Left;
            _dragStartTop = Top;
            CaptureMouse();
            e.Handled = true;
        }

        private void LauncherMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
            Point now = PointToScreen(e.GetPosition(this));
            double dx = now.X - _dragStartScreen.X;
            double dy = now.Y - _dragStartScreen.Y;
            if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < 6) return;
            _dragMoved = true;
            Left = _dragStartLeft + dx;
            Top = _dragStartTop + dy;
        }

        private void LauncherMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            ReleaseMouseCapture();
            bool moved = _dragMoved;
            _dragging = false;
            _dragMoved = false;
            if (moved)
            {
                Rect rect; IntPtr handle;
                if (TryFindHearthstone(out rect, out handle) && _store != null)
                {
                    double rx = (Left - rect.Left) / Math.Max(1.0, rect.Width);
                    double ry = (Top - rect.Top) / Math.Max(1.0, rect.Height);
                    _store.SaveLauncherPosition(new Point(Math.Max(0.0, Math.Min(0.94, rx)), Math.Max(0.0, Math.Min(0.94, ry))));
                }
            }
            else if (_openBuilder != null)
            {
                _openBuilder();
            }
            e.Handled = true;
        }

        private void ApplyLauncherStyle()
        {
            try
            {
                var src = PresentationSource.FromVisual(this) as HwndSource;
                if (src == null) return;
                IntPtr h = src.Handle;
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            } catch { }
        }

        private static bool TryFindHearthstone(out Rect rect, out IntPtr handle)
        {
            Native.RECT r;
            if (Native.TryFindHearthstoneWindow(out r, out handle)) { rect = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top); return true; }
            rect = Rect.Empty; return false;
        }
    }


    internal sealed class BattlegroundsLobbyInfoWindow : Window
    {
        private sealed class LobbyHeroInfo
        {
            public int Id;
            public string CardId;
            public int Health;
            public int TavernTier;
            public string Tribe;
            public int TribeCount;
            public int PortraitOrder;
            public bool IsSelf;
            public bool IsDead;
            public bool IsUnresolved;
            public bool LevelUpPending;
            public int DuoTeam;
            public int PlayerId;
            public int DuoTeammatePlayerId;
            public bool DuoFightsFirstKnown;
            public bool DuoFightsFirst;
        }

        private sealed class HeroRuntimeState
        {
            public int PlayerId;
            public int EntityId;
            public string CardId;
            public int CurrentHealth;
            public bool HasHealthData;
            public bool IsHeroInPlay;
            public int TavernTier;
            public int DuoTeam;
            public bool DuoTeamKnown;
            public int DuoTeammatePlayerId;
            public bool DuoFightsFirstKnown;
            public bool DuoFightsFirst;
            public int LeaderboardPlace;
        }

        // PLAYER_TECH_LEVEL and the Duo/leaderboard tags live on the TYPE_PLAYER entity, not on the
        // hero minion entity. Reading them only from hero-like entities (as before) left these fields
        // at 0 for every non-local player, which silently forced the rail to fall back to the
        // hover-only native "recent combats" panel for tier. See RefreshHeroRuntimeCacheIfNeeded.
        private sealed class PlayerTagState
        {
            public int TavernTier;
            public bool DuoTeamKnown;
            public int DuoTeam;
            public int DuoTeammatePlayerId;
            public bool DuoFightsFirstKnown;
            public bool DuoFightsFirst;
            public int LeaderboardPlace;
        }

        private readonly StackPanel _panel = new StackPanel();
        private readonly List<Border> _rows = new List<Border>();
        private const int MaxRows = 8;
        private bool _isDuoMode;
        private const double DefaultRailLeftRatio = 0.012;
        private const double DefaultRailTopRatio = 0.092;
        private const double PanelWidth = 94;
        private const double SkipCombatButtonBlockHeight = 34;
        private const int RefreshMs = 240;
        private const string PositionFileName = "bg-lobby-panel.position";
        private double _offsetXNorm;
        private double _offsetYNorm;
        private bool _hasCustomPosition;
        private bool _positionLoaded;
        private DateTime _lastUpdate = DateTime.MinValue;
        private string _lastRenderSignature;
        private Rect _lastGameRect = Rect.Empty;
        private readonly List<LobbyHeroInfo> _cachedSnapshot = new List<LobbyHeroInfo>();
        private int _lastObservedTurn = -1;
        private DateTime _snapshotRetryUntil = DateTime.MinValue;
        private DateTime _nextSnapshotRetry = DateTime.MinValue;
        private int _snapshotRetryCount;
        private DateTime _lastMirrorDiag = DateTime.MinValue;
        private const int SnapshotRetryWindowMs = 1800;
        private const int SnapshotRetryIntervalMs = 180;
        private const int MaxSnapshotRetries = 10;
        private const int NativeRailPollMs = 500;
        private const int NativeRailPollInitialDelayMs = 250;
        private DateTime _lastNativeRailPoll = DateTime.MinValue;
        private DateTime _nativeRailPollNotBefore = DateTime.MinValue;
        private string _lastNativeDataSignature;
        private DateTime _lastNativeDataChange = DateTime.MinValue;
        private int _nativeRebindTurn = -1;
        private int _nativeRebindCountThisTurn;
        private bool _dragging;
        private Point _dragStartScreen;
        private double _dragStartLeft;
        private double _dragStartTop;
        // RAIL V3: feed the native leaderboard one seat at a time instead of
        // re-reading every hero on every poll. This keeps updates responsive
        // while allowing a not-yet-ready hero to be revisited later.
        private int _nativeSeatCursor;
        private readonly int[] _nativeSeatMissingStreak = new int[MaxRows];
        private readonly DateTime[] _nativeSeatRetryAt = new DateTime[MaxRows];
        private DateTime _heroRuntimeCacheAt = DateTime.MinValue;
        private readonly List<HeroRuntimeState> _heroRuntimeCache = new List<HeroRuntimeState>();
        private int _runtimeSelfPlayerId;
        private string _runtimeSelfHeroCardId;
        private const int LiveSeatBatchSize = 2;
        private readonly Dictionary<string, int> _tierBaselineByIdentity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _levelUpTurnByIdentity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // V26: lock Solo/Duo mode from the initial reliable leaderboard shape.
        private bool _modeLocked;
        private DateTime _lastDuoOrderRefresh = DateTime.MinValue;
        private readonly int[] _duoVisualToNativeSeat = Enumerable.Range(0, MaxRows).ToArray();

        public BattlegroundsLobbyInfoWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Cursor = Cursors.SizeAll;
            Width = PanelWidth;
            Height = 8 * 34 + SkipCombatButtonBlockHeight;
            _panel.Orientation = Orientation.Vertical;
            Content = _panel;
            // The panel itself is directly draggable (grab it anywhere and move it) instead of a
            // separate drag-handle window, which never behaved reliably. Child rows stay
            // IsHitTestVisible=false so mouse-down anywhere on the panel reaches these handlers.
            MouseLeftButtonDown += HandleMouseDown;
            MouseMove += HandleMouseMove;
            MouseLeftButtonUp += HandleMouseUp;
            // If the OS revokes our mouse capture mid-drag (a full-screen game grabbing input,
            // an alt-tab, a display/DPI change recreating the HWND) MouseLeftButtonUp never fires.
            // Without this, _dragging stays stuck true forever: ApplyPosition() stops repositioning
            // the panel every tick, and every later click - including on Skip Combat - is treated
            // as a fresh drag instead of reaching the button.
            LostMouseCapture += HandleLostMouseCapture;
            var skipCombatButton = new Border
            {
                Width = PanelWidth - 2,
                // Was 20px tall - a thin target to land a precise click on. Bumped to 30px
                // (matching the block height below) so the whole clickable area is bigger.
                Height = 30,
                Margin = new Thickness(0, 0, 0, 4),
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.FromArgb(150, 18, 12, 28)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(170, 150, 95, 220)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = "Skip the combat replay: blocks Hearthstone's outbound network traffic for ~3s (requires HDT running as Administrator) so the server treats it like a dropped connection and you land straight in the shop. Experimental - occasionally needs two reconnect attempts.",
                Child = new TextBlock
                {
                    Text = "⟲ Skip Combat",
                    Foreground = Brushes.White,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            skipCombatButton.MouseLeftButtonDown += SkipCombatButtonClick;
            _panel.Children.Add(skipCombatButton);
            for (int i = 0; i < MaxRows; i++)
            {
                var row = new Border
                {
                    Width = PanelWidth - 2,
                    Height = 32,
                    Margin = new Thickness(0, 0, 0, 2),
                    CornerRadius = new CornerRadius(5),
                    Background = new SolidColorBrush(Color.FromArgb(150, 18, 12, 28)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(170, 150, 95, 220)),
                    BorderThickness = new Thickness(1),
                    Visibility = Visibility.Collapsed,
                    IsHitTestVisible = false,
                    Padding = new Thickness(3, 0, 3, 0)
                };
                _rows.Add(row);
                _panel.Children.Add(row);
            }
            for (int i = 0; i < MaxRows; i++)
                _cachedSnapshot.Add(new LobbyHeroInfo { PortraitOrder = i + 1, IsUnresolved = true });
            Loaded += delegate { ApplyNoActivate(); LoadPosition(); };
            Closed += delegate { SavePosition(); };
        }

        public void UpdateForCurrentGame(object game)
        {
            if ((DateTime.UtcNow - _lastUpdate).TotalMilliseconds < RefreshMs) return;
            _lastUpdate = DateTime.UtcNow;
            int perfFetchCount = 0;

            if (game == null || !PluginReflection.GetBool(game, "IsBattlegroundsMatch") ||
                PluginReflection.GetBool(game, "IsInMenu") || !Native.IsForegroundHearthstone())
            {
                ResetSnapshotState();
                HideAll();
                Hide();
                return;
            }

            Rect rect;
            IntPtr handle;
            if (!TryFindHearthstone(out rect, out handle))
            {
                ResetSnapshotState();
                HideAll();
                Hide();
                return;
            }

            if (!_positionLoaded) LoadPosition();

            // RAIL V2: native leaderboard only. No board scan, no race reconstruction.
            // The three native values are read by BattlegroundsScryMemory:
            // m_singleTribeWithCountName, m_singleTribeWithCountNumber, m_techLevelCount.
            //
            // IMPORTANT: the previous V1 used a 10s data poll without resetting the native
            // snapshot at the start of a new turn. That could leave the rail showing the old
            // unresolved skeleton for the whole next turn. We restore the low-latency UI loop
            // while forcing an immediate native refresh + short retries on every turn change.
            int turn = GetBattlegroundsTurnNumber(game);
            DateTime now = DateTime.UtcNow;
            bool turnChanged = turn > 0 && turn != _lastObservedTurn;
            if (turnChanged)
            {
                _lastObservedTurn = turn;
                _snapshotRetryUntil = now.AddMilliseconds(SnapshotRetryWindowMs);
                _nextSnapshotRetry = now;
                _snapshotRetryCount = 0;
                // Hypothesis/fix V17: LevelUpPending is a per-turn signal, not a persistent property.
                // Reset it at the authoritative Battlegrounds turn boundary; a new tier increase
                // observed later in this same turn will set it again in ApplyNativeSeatTile().
                for (int i = 0; i < _cachedSnapshot.Count; i++)
                    _cachedSnapshot[i].LevelUpPending = false;
                // Keep tier baselines across turns. A first fresh read in the new turn must still
                // be able to detect T4 -> T5 and display the level-up arrow immediately.
                _levelUpTurnByIdentity.Clear();
                _lastNativeRailPoll = DateTime.MinValue;
                _lastNativeDataSignature = null;
                _lastNativeDataChange = now;
                _nativeRebindTurn = turn;
                _nativeRebindCountThisTurn = 0;
                try
                {
                    BattlegroundsScryMemory.Instance.ForceRebind();
                    _nativeRebindCountThisTurn = 1;
                }
                catch { }
                _lastRenderSignature = null;
                EnsureSkeletonRows();
                for (int i = 0; i < _cachedSnapshot.Count; i++)
                    _cachedSnapshot[i].LevelUpPending = false;
            }

            bool retryWindowActive = turn > 0 && now <= _snapshotRetryUntil && _snapshotRetryCount < MaxSnapshotRetries;

            if (turn > 0 && !retryWindowActive &&
                (now - _lastNativeDataChange).TotalMilliseconds >= 1200 &&
                _nativeRebindTurn == turn && _nativeRebindCountThisTurn < 2)
            {
                try
                {
                    BattlegroundsScryMemory.Instance.ForceRebind();
                    _nativeRebindCountThisTurn++;
                    _lastNativeDataChange = now;
                    _snapshotRetryUntil = now.AddMilliseconds(SnapshotRetryWindowMs);
                    _nextSnapshotRetry = now;
                    _snapshotRetryCount = 0;
                    _nativeSeatCursor = 0;
                }
                catch { }
            }

            if (!_modeLocked || (_isDuoMode && (now - _lastDuoOrderRefresh).TotalMilliseconds >= 750))
                RefreshLobbyModeAndDuoMapping(game, now);

            // RAIL V3: do not fetch all heroes at once. One seat is read per UI tick,
            // then the cursor advances. Missing/empty seats are treated as dead only
            // after a few consecutive misses, so the initial leaderboard population
            // does not create false deaths. A seat whose native tribe/tier is not ready
            // is revisited on the next cycle instead of stalling the whole rail.
            bool anySeatDue = turn > 0 && (turnChanged || _cachedSnapshot.Count == 0 || HasAnySeatDue(now));
            if (anySeatDue)
            {
                for (int batch = 0; batch < LiveSeatBatchSize; batch++)
                {
                    int seatIndex = FindNextWorkableSeat(_nativeSeatCursor, now, turnChanged);
                    if (seatIndex < 0) break;
                    _nativeSeatCursor = seatIndex;
                    int nativeSeatIndex = seatIndex;
                    if (_modeLocked && _isDuoMode)
                        nativeSeatIndex = _duoVisualToNativeSeat[Math.Max(0, Math.Min(MaxRows - 1, seatIndex))];
                    BattlegroundsScryMemory.RailTile tile = BattlegroundsScryMemory.Instance.ReadLeaderboardTileForTeam(nativeSeatIndex);
                    EnrichRailTileFromRuntime(tile);
                    perfFetchCount++;
                    if (tile != null)
                    {
                        string nativeSig = string.Concat(tile.Team, "|", tile.HeroCardId ?? "", "|", tile.NativeTribe ?? "", "|", tile.NativeCount, "|", tile.NativeTier);
                        if (!string.Equals(_lastNativeDataSignature, nativeSig, StringComparison.Ordinal))
                        {
                            _lastNativeDataSignature = nativeSig;
                            _lastNativeDataChange = now;
                        }
                        LobbyHeroInfo before = EnsureSnapshotRow(seatIndex);
                        string beforeTribe = before.Tribe ?? "Neutral";
                        int beforeCount = before.TribeCount;
                        int beforeTier = before.TavernTier;
                        bool cardChanged = !string.IsNullOrWhiteSpace(tile.HeroCardId) && !string.Equals(before.CardId, tile.HeroCardId, StringComparison.OrdinalIgnoreCase);
                        ApplyNativeSeatTile(tile, seatIndex);
                        _nativeSeatMissingStreak[seatIndex] = 0;
                        string normalizedNativeTribe = NormalizeLobbyTribe(tile.NativeTribe);
                    bool nativeTribeFieldKnown = IsKnownLobbyTribe(normalizedNativeTribe);
                    // count==0 is a valid, resolved zero-count state, but it must not promote the
                    // native race label to a visible faction. Tier is still authoritative for the row.
                    bool nativeReady = tile.NativeCount >= 0 && tile.NativeCount <= 7 &&
                                       tile.NativeTier > 0 && (nativeTribeFieldKnown || tile.NativeCount == 0);
                        _nativeSeatRetryAt[seatIndex] = now.AddMilliseconds(nativeReady ? NativeRailPollMs : 250);
                        _nativeSeatCursor = (seatIndex + 1) % MaxRows;
                        _snapshotRetryCount = 0;
                    }
                    else
                    {
                        _nativeSeatMissingStreak[seatIndex]++;
                        _nativeSeatRetryAt[seatIndex] = now.AddMilliseconds(SnapshotRetryIntervalMs);
                        bool classifiedDead = _nativeSeatMissingStreak[seatIndex] >= 4 && now > _snapshotRetryUntil;
                        if (classifiedDead)
                        {
                            LobbyHeroInfo deadInfo = EnsureSnapshotRow(seatIndex);
                            deadInfo.IsDead = true;
                            deadInfo.IsUnresolved = false;
                            deadInfo.Tribe = "Neutral";
                            deadInfo.TribeCount = 0;
                            deadInfo.LevelUpPending = false;
                            _nativeSeatCursor = (seatIndex + 1) % MaxRows;
                        }
                    }
                    _lastNativeRailPoll = now;
                }
                _lastRenderSignature = null;
            }

            EnsureSkeletonRows();

            double rowHeight = Math.Max(30.0, Math.Min(36.0, rect.Height * 0.031));
            Width = PanelWidth;
            Height = rowHeight * MaxRows + Math.Max(0, MaxRows - 1) * 2 + SkipCombatButtonBlockHeight;

            bool geometryChanged = _lastGameRect != rect;
            _lastGameRect = rect;
            // Never fight the user's own drag: while dragging, HandleMouseMove() is already the sole
            // authority over Left/Top for this tick. Re-deriving position from the anchor+offset here
            // on every ~240ms poll raced against live mouse movement and made the drag feel broken.
            if (!_dragging)
                ApplyPosition(rect, geometryChanged);

            List<LobbyHeroInfo> displaySnapshot = BuildDisplaySnapshot(_cachedSnapshot);
            string signature = BuildRenderSignature(displaySnapshot, rowHeight);
            if (!string.Equals(signature, _lastRenderSignature, StringComparison.Ordinal))
            {
                RenderRows(displaySnapshot, rowHeight);
                _lastRenderSignature = signature;
            }


            if (Visibility != Visibility.Visible) Show();
        }

        private int FindNextWorkableSeat(int start, DateTime now, bool turnChanged)
        {
            for (int offset = 0; offset < MaxRows; offset++)
            {
                int idx = (start + offset + MaxRows) % MaxRows;
                LobbyHeroInfo info = idx < _cachedSnapshot.Count ? _cachedSnapshot[idx] : null;
                if (info != null && info.IsDead && !turnChanged) continue;
                if (turnChanged || info == null || now >= _nativeSeatRetryAt[idx]) return idx;
            }
            return -1;
        }

        private LobbyHeroInfo EnsureSnapshotRow(int seatIndex)
        {
            while (_cachedSnapshot.Count < MaxRows)
                _cachedSnapshot.Add(new LobbyHeroInfo { PortraitOrder = _cachedSnapshot.Count + 1, IsUnresolved = true });
            LobbyHeroInfo info = _cachedSnapshot[seatIndex];
            if (info.PortraitOrder <= 0) info.PortraitOrder = seatIndex + 1;
            return info;
        }

        private void ApplyNativeSeatTile(BattlegroundsScryMemory.RailTile tile, int seatIndex)
        {
            LobbyHeroInfo info = EnsureSnapshotRow(seatIndex);
            RefreshHeroRuntimeCacheIfNeeded(HDTCore.Game);

            bool mixedTribe = string.Equals(tile.NativeTribe, "Mixed", StringComparison.OrdinalIgnoreCase);
            string tribe = mixedTribe ? "Mixed" : NormalizeLobbyTribe(tile.NativeTribe);
            bool validTribe = !mixedTribe && IsKnownLobbyTribe(tribe);
            int count = tile.NativeCount;
            string previousCard = info.CardId;
            int previousTier = info.TavernTier;
            HeroRuntimeState runtime = FindHeroRuntimeState(tile);
            // V26: native rail tier is authoritative. The old hero-tag/HDT fallback chain is
            // removed from the live rail because it could reintroduce stale values until hover.
            int tier = runtime != null && runtime.TavernTier >= 1 && runtime.TavernTier <= 6
                ? runtime.TavernTier
                : (tile.NativeTier >= 1 && tile.NativeTier <= 6 ? tile.NativeTier : 0);
            string resolvedTierSource = runtime != null && runtime.TavernTier >= 1 && runtime.TavernTier <= 6
                ? "game-entity"
                : (tile.NativeTier >= 1 && tile.NativeTier <= 6 ? "native-rail" : "none");
            // Tavern Tier never decreases during a live match. Keep a previously confirmed
            // higher value for the same hero identity when a native source temporarily lags.
            if (!string.IsNullOrWhiteSpace(tile.HeroCardId) && !string.IsNullOrWhiteSpace(info.CardId) &&
                SelfCardIdsMatch(info.CardId, tile.HeroCardId) && previousTier > tier)
            {
                tier = previousTier;
                resolvedTierSource = resolvedTierSource + "+held-previous";
            }
            // V18: tribe is visible only when there is positive unit-count evidence.
            // Tier is resolved independently because a valid tier can exist before race-count
            // data is populated. This prevents false Murloc/neutral icons while preserving T# data.
            bool displayTribe = validTribe && count > 0;

            bool isSelf = IsRuntimeSelf(tile, runtime);

            info.Id = tile.EntityId;
            info.CardId = tile.HeroCardId;
            info.DuoTeam = tile.Team;
            info.PlayerId = runtime != null && runtime.PlayerId > 0 ? runtime.PlayerId : tile.PlayerId;
            info.DuoTeammatePlayerId = runtime != null && runtime.DuoTeammatePlayerId > 0 ? runtime.DuoTeammatePlayerId : tile.DuoTeammatePlayerId;
            info.DuoFightsFirstKnown = runtime != null && runtime.DuoFightsFirstKnown ? true : tile.DuoFightsFirstKnown;
            info.DuoFightsFirst = runtime != null && runtime.DuoFightsFirstKnown ? runtime.DuoFightsFirst : tile.DuoFightsFirst;
            info.Health = runtime != null ? runtime.CurrentHealth : (isSelf ? 1 : info.Health);

            // Preserve the V8 native values exactly; only replace stale data when a valid native value arrives.
            // An empty native panel during initialisation is a soft miss, not evidence of a new Neutral comp.
            if (mixedTribe)
            {
                info.Tribe = "Mixed";
                info.TribeCount = 0;
                info.IsUnresolved = false;
            }
            else if (displayTribe && count >= 1 && count <= 7)
            {
                info.Tribe = tribe;
                info.TribeCount = count;
                info.IsUnresolved = false;
            }
            else if (count == 0 || !validTribe)
            {
                // Do not carry a stale tribe/icon across a zero-count or invalid native result.
                // Keep the row resolved when a valid tavern tier exists so the tier remains visible.
                info.Tribe = "Neutral";
                info.TribeCount = 0;
                info.IsUnresolved = tier <= 0;
            }

            string identityKey = BuildHeroIdentityKey(tile, info, seatIndex);
            bool cardIsNew = !string.IsNullOrWhiteSpace(tile.HeroCardId) &&
                             !string.IsNullOrWhiteSpace(previousCard) &&
                             !SelfCardIdsMatch(previousCard, tile.HeroCardId);
            if (tier > 0)
            {
                int baseline;
                if (!_tierBaselineByIdentity.TryGetValue(identityKey, out baseline) || cardIsNew)
                {
                    _tierBaselineByIdentity[identityKey] = tier;
                    info.LevelUpPending = _levelUpTurnByIdentity.TryGetValue(identityKey, out int flagTurn) && flagTurn == _lastObservedTurn;
                }
                else
                {
                    if (tier > baseline)
                    {
                        _levelUpTurnByIdentity[identityKey] = _lastObservedTurn;
                        info.LevelUpPending = true;
                        _tierBaselineByIdentity[identityKey] = tier;
                    }
                    else if (tier < baseline)
                    {
                        _tierBaselineByIdentity[identityKey] = tier;
                        info.LevelUpPending = false;
                    }
                    else
                    {
                        int flagTurn;
                        info.LevelUpPending = _levelUpTurnByIdentity.TryGetValue(identityKey, out flagTurn) && flagTurn == _lastObservedTurn;
                    }
                }
                info.TavernTier = tier;
            }
            else if (previousTier > 0)
            {
                info.TavernTier = previousTier;
                info.LevelUpPending = _levelUpTurnByIdentity.ContainsKey(identityKey) && _levelUpTurnByIdentity[identityKey] == _lastObservedTurn;
            }

            info.PortraitOrder = seatIndex + 1;
            info.IsSelf = isSelf;

            if (!isSelf && runtime != null && runtime.HasHealthData && runtime.CurrentHealth <= 0)
            {
                info.IsDead = true;
                info.IsUnresolved = false;
                info.LevelUpPending = false;
            }
            else
            {
                info.IsDead = false;
            }
        }

        private static string BuildHeroIdentityKey(BattlegroundsScryMemory.RailTile tile, LobbyHeroInfo info, int seatIndex)
        {
            if (tile != null && tile.PlayerId > 0) return "P:" + tile.PlayerId;
            if (info != null && info.PlayerId > 0) return "P:" + info.PlayerId;
            if (tile != null && tile.EntityId > 0) return "E:" + tile.EntityId;
            if (tile != null && !string.IsNullOrWhiteSpace(tile.HeroCardId)) return "C:" + tile.HeroCardId;
            return "S:" + seatIndex;
        }

        private bool IsRuntimeSelf(BattlegroundsScryMemory.RailTile tile, HeroRuntimeState runtime)
        {
            if (_runtimeSelfPlayerId > 0 && tile.PlayerId > 0 && tile.PlayerId == _runtimeSelfPlayerId) return true;
            if (runtime != null && runtime.PlayerId > 0 && _runtimeSelfPlayerId > 0 && runtime.PlayerId == _runtimeSelfPlayerId) return true;
            if (!string.IsNullOrWhiteSpace(_runtimeSelfHeroCardId) && !string.IsNullOrWhiteSpace(tile.HeroCardId) && SelfCardIdsMatch(_runtimeSelfHeroCardId, tile.HeroCardId)) return true;
            return runtime != null && !string.IsNullOrWhiteSpace(_runtimeSelfHeroCardId) && SelfCardIdsMatch(_runtimeSelfHeroCardId, runtime.CardId);
        }

        private static bool SelfCardIdsMatch(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            string na = NormalizeSelfHeroCardId(a);
            string nb = NormalizeSelfHeroCardId(b);
            return !string.IsNullOrWhiteSpace(na) && !string.IsNullOrWhiteSpace(nb) && string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeSelfHeroCardId(string id)
        {
            string s = id == null ? string.Empty : id.Trim();
            int skin = s.IndexOf("_SKIN_", StringComparison.OrdinalIgnoreCase);
            if (skin > 0) s = s.Substring(0, skin);
            return s;
        }

        // Local reflection path helper for the lobby window. The Scry-memory helper
        // lives in a different class and therefore cannot be called directly here.
        private static object ReadObjectPath(dynamic root, params string[] path)
        {
            try
            {
                dynamic cur = root;
                foreach (string part in path)
                    cur = cur?[part];
                return cur;
            }
            catch { return null; }
        }

        private void RefreshHeroRuntimeCacheIfNeeded(object game)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _heroRuntimeCacheAt).TotalMilliseconds < 200) return;
            _heroRuntimeCacheAt = now;
            _heroRuntimeCache.Clear();
            _runtimeSelfPlayerId = 0;
            _runtimeSelfHeroCardId = null;
            try
            {
                object playerEntity = PluginReflection.GetPropertyObject(game, "PlayerEntity");
                _runtimeSelfPlayerId = FirstPositive(
                    PluginReflection.GetTagValueByNames(playerEntity, new[] { "CONTROLLER", "PLAYER_ID", "BACON_PLAYER_ID" }),
                    PluginReflection.GetInt(playerEntity, "Controller"),
                    PluginReflection.GetInt(playerEntity, "Id"),
                    PluginReflection.GetInt(playerEntity, "EntityId"));
                _runtimeSelfHeroCardId = FirstText(PluginReflection.GetString(playerEntity, "CardId"), PluginReflection.GetString(playerEntity, "CardID"));

                var playerTagsById = new Dictionary<int, PlayerTagState>();
                foreach (object entity in PluginReflection.EnumerateEntities(game))
                {
                    if (entity == null || !PluginReflection.GetBool(entity, "IsPlayer")) continue;
                    int pid = FirstPositive(PluginReflection.GetInt(entity, "Id"), PluginReflection.GetInt(entity, "EntityId"));
                    if (pid <= 0) continue;
                    bool duoTeamKnownP = PluginReflection.HasTag(entity, "BACON_DUO_TEAM_ID") || PluginReflection.HasTag(entity, "DUO_TEAM_ID");
                    bool fightsFirstKnownP = PluginReflection.HasTag(entity, "BACON_DUO_PLAYER_FIGHTS_FIRST_NEXT_COMBAT") || PluginReflection.HasTag(entity, "DUO_PLAYER_FIGHTS_FIRST_NEXT_COMBAT");
                    playerTagsById[pid] = new PlayerTagState
                    {
                        TavernTier = PluginReflection.GetTagValueByNames(entity, new[] { "PLAYER_TECH_LEVEL", "TECH_LEVEL", "BG_TECH_LEVEL", "BACON_TECH_LEVEL", "TAVERN_TIER" }),
                        DuoTeamKnown = duoTeamKnownP,
                        DuoTeam = duoTeamKnownP ? PluginReflection.GetTagValueByNames(entity, new[] { "BACON_DUO_TEAM_ID", "DUO_TEAM_ID" }) : -1,
                        DuoTeammatePlayerId = PluginReflection.GetTagValueByNames(entity, new[] { "BACON_DUO_TEAMMATE_PLAYER_ID", "DUO_TEAMMATE_PLAYER_ID" }),
                        DuoFightsFirstKnown = fightsFirstKnownP,
                        DuoFightsFirst = PluginReflection.GetTagValueByNames(entity, new[] { "BACON_DUO_PLAYER_FIGHTS_FIRST_NEXT_COMBAT", "DUO_PLAYER_FIGHTS_FIRST_NEXT_COMBAT" }) > 0,
                        LeaderboardPlace = PluginReflection.GetTagValueByNames(entity, new[] { "PLAYER_LEADERBOARD_PLACE", "LEADERBOARD_PLACE" })
                    };
                }

                foreach (object entity in PluginReflection.EnumerateEntities(game))
                {
                    if (entity == null) continue;
                    string card = FirstText(PluginReflection.GetString(entity, "CardId"), PluginReflection.GetString(entity, "CardID"));
                    bool heroLike = PluginReflection.GetBool(entity, "IsHero") ||
                                     (!string.IsNullOrWhiteSpace(card) && card.IndexOf("HERO", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!heroLike) continue;
                    int playerId = FirstPositive(PluginReflection.GetTagValueByNames(entity, new[] { "PLAYER_ID", "BACON_PLAYER_ID" }),
                        PluginReflection.GetInt(entity, "PlayerId"), PluginReflection.GetInt(entity, "Controller"));
                    int entityId = FirstPositive(PluginReflection.GetInt(entity, "Id"), PluginReflection.GetInt(entity, "EntityId"));
                    bool hasHealthDisplayTag = PluginReflection.HasTag(entity, "HEALTH_DISPLAY");
                    bool hasHealthTag = PluginReflection.HasTag(entity, "HEALTH");
                    int healthDisplay = PluginReflection.GetTagValueByNames(entity, new[] { "HEALTH_DISPLAY" });
                    int baseHealth = PluginReflection.GetTagValueByNames(entity, new[] { "HEALTH" });
                    int damage = PluginReflection.GetTagValueByNames(entity, new[] { "DAMAGE" });
                    bool hasHealthData = hasHealthDisplayTag || hasHealthTag || healthDisplay > 0 || baseHealth > 0;
                    int currentHealth = hasHealthDisplayTag ? healthDisplay : (hasHealthTag ? Math.Max(0, baseHealth - damage) : (healthDisplay > 0 ? healthDisplay : (baseHealth > 0 ? Math.Max(0, baseHealth - damage) : 0)));
                    int heroTier = PluginReflection.GetTagValueByNames(entity, new[] { "PLAYER_TECH_LEVEL", "TECH_LEVEL", "BG_TECH_LEVEL", "BACON_TECH_LEVEL", "TAVERN_TIER" });
                    bool duoTeamKnown = PluginReflection.HasTag(entity, "BACON_DUO_TEAM_ID") || PluginReflection.HasTag(entity, "DUO_TEAM_ID");
                    int duoTeam = duoTeamKnown ? PluginReflection.GetTagValueByNames(entity, new[] { "BACON_DUO_TEAM_ID", "DUO_TEAM_ID" }) : -1;
                    int duoMate = PluginReflection.GetTagValueByNames(entity, new[] { "BACON_DUO_TEAMMATE_PLAYER_ID", "DUO_TEAMMATE_PLAYER_ID" });
                    int leaderboardPlace = PluginReflection.GetTagValueByNames(entity, new[] { "PLAYER_LEADERBOARD_PLACE", "LEADERBOARD_PLACE" });
                    bool hasFightsFirstTag = PluginReflection.HasTag(entity, "BACON_DUO_PLAYER_FIGHTS_FIRST_NEXT_COMBAT") || PluginReflection.HasTag(entity, "DUO_PLAYER_FIGHTS_FIRST_NEXT_COMBAT");
                    int fightsFirstRaw = PluginReflection.GetTagValueByNames(entity, new[] { "BACON_DUO_PLAYER_FIGHTS_FIRST_NEXT_COMBAT", "DUO_PLAYER_FIGHTS_FIRST_NEXT_COMBAT" });

                    PlayerTagState playerTags;
                    if (playerId > 0 && playerTagsById.TryGetValue(playerId, out playerTags))
                    {
                        if (playerTags.TavernTier > 0) heroTier = playerTags.TavernTier;
                        if (playerTags.DuoTeamKnown) { duoTeamKnown = true; duoTeam = playerTags.DuoTeam; }
                        if (playerTags.DuoTeammatePlayerId > 0) duoMate = playerTags.DuoTeammatePlayerId;
                        if (playerTags.DuoFightsFirstKnown) { hasFightsFirstTag = true; fightsFirstRaw = playerTags.DuoFightsFirst ? 1 : 0; }
                        if (playerTags.LeaderboardPlace > 0) leaderboardPlace = playerTags.LeaderboardPlace;
                    }

                    _heroRuntimeCache.Add(new HeroRuntimeState { PlayerId = playerId, EntityId = entityId, CardId = card, CurrentHealth = currentHealth, HasHealthData = hasHealthData, IsHeroInPlay = PluginReflection.GetBool(entity, "IsInPlay"), TavernTier = heroTier, DuoTeam = duoTeam, DuoTeamKnown = duoTeamKnown, DuoTeammatePlayerId = duoMate, DuoFightsFirstKnown = hasFightsFirstTag, DuoFightsFirst = fightsFirstRaw > 0, LeaderboardPlace = leaderboardPlace });
                }
            }
            catch { }
        }

        private HeroRuntimeState FindHeroRuntimeState(BattlegroundsScryMemory.RailTile tile)
        {
            if (tile == null) return null;
            HeroRuntimeState best = null;
            if (tile.PlayerId > 0) best = _heroRuntimeCache.FirstOrDefault(x => x.PlayerId == tile.PlayerId);
            if (best == null && tile.EntityId > 0) best = _heroRuntimeCache.FirstOrDefault(x => x.EntityId == tile.EntityId);
            if (best == null && !string.IsNullOrWhiteSpace(tile.HeroCardId))
            {
                var matches = _heroRuntimeCache.Where(x => SelfCardIdsMatch(x.CardId, tile.HeroCardId)).ToList();
                if (matches.Count == 1) best = matches[0];
                else if (matches.Count > 1)
                {
                    var teamMatches = matches.Where(x => x.DuoTeamKnown && x.DuoTeam == tile.Team).ToList();
                    if (teamMatches.Count == 1) best = teamMatches[0];
                    else best = matches.OrderBy(x => x.LeaderboardPlace > 0 ? x.LeaderboardPlace : int.MaxValue)
                        .ThenByDescending(x => x.IsHeroInPlay).ThenByDescending(x => x.CurrentHealth).FirstOrDefault();
                }
            }
            return best;
        }

        private void EnrichRailTileFromRuntime(BattlegroundsScryMemory.RailTile tile)
        {
            if (tile == null) return;
            var runtime = FindHeroRuntimeState(tile);
            if (runtime == null) return;
            if (runtime.PlayerId > 0) tile.PlayerId = runtime.PlayerId;
            if (runtime.EntityId > 0) tile.EntityId = runtime.EntityId;
            if (runtime.DuoTeamKnown) tile.Team = runtime.DuoTeam;
            if (runtime.DuoTeammatePlayerId > 0) tile.DuoTeammatePlayerId = runtime.DuoTeammatePlayerId;
            if (runtime.DuoFightsFirstKnown) { tile.DuoFightsFirstKnown = true; tile.DuoFightsFirst = runtime.DuoFightsFirst; }
            if (runtime.TavernTier > 0) tile.HeroEntityTier = runtime.TavernTier;
        }

        private void RefreshLobbyModeAndDuoMapping(object game, DateTime now)
        {
            try
            {
                var rail = BattlegroundsScryMemory.Instance.ReadLeaderboardTiles();
                if (rail == null || rail.Count == 0) return;
                var groups = rail.GroupBy(x => x.Team).OrderBy(g => g.Key).ToList();
                bool duoReliable = rail.Count >= 6 && groups.Any(g => g.Count() >= 2);
                bool soloReliable = rail.Count >= MaxRows && groups.All(g => g.Count() == 1);

                if (!_modeLocked)
                {
                    if (duoReliable) { _isDuoMode = true; _modeLocked = true; }
                    else if (soloReliable) { _isDuoMode = false; _modeLocked = true; }
                }
                if (!_modeLocked || !_isDuoMode) { _lastDuoOrderRefresh = now; return; }

                RefreshHeroRuntimeCacheIfNeeded(game);
                for (int i = 0; i < MaxRows; i++) _duoVisualToNativeSeat[i] = i;

                // Native team groups provide the four team blocks. Within each block, use the
                // explicit game-entity FIGHTS_FIRST bit when available. No arbitrary reversal.
                foreach (var g in groups.Where(g => g.Count() == 2))
                {
                    var native = g.OrderBy(x => x.Order).ToList();
                    var runtimes = native.Select(t => new { Tile = t, Runtime = FindHeroRuntimeState(t) }).ToList();
                    if (runtimes.Count != 2 || runtimes.Any(x => x.Runtime == null || !x.Runtime.DuoFightsFirstKnown)) continue;
                    var first = runtimes.SingleOrDefault(x => x.Runtime.DuoFightsFirst);
                    var second = runtimes.SingleOrDefault(x => !x.Runtime.DuoFightsFirst);
                    if (first == null || second == null) continue;
                    int start = native.Min(x => x.Order);
                    _duoVisualToNativeSeat[start] = first.Tile.Order;
                    _duoVisualToNativeSeat[start + 1] = second.Tile.Order;
                }

                _lastDuoOrderRefresh = now;
            }
            catch { }
        }

        private bool HasAnySeatDue(DateTime now)
        {
            for (int i = 0; i < MaxRows; i++)
            {
                LobbyHeroInfo info = i < _cachedSnapshot.Count ? _cachedSnapshot[i] : null;
                if (info != null && info.IsDead) continue;
                if (now >= _nativeSeatRetryAt[i]) return true;
            }
            return false;
        }

        private void EnsureSkeletonRows()
        {
            while (_cachedSnapshot.Count < MaxRows)
                _cachedSnapshot.Add(new LobbyHeroInfo { PortraitOrder = _cachedSnapshot.Count + 1, IsUnresolved = true });
            if (_cachedSnapshot.Count > MaxRows) _cachedSnapshot.RemoveRange(MaxRows, _cachedSnapshot.Count - MaxRows);
            for (int i = 0; i < MaxRows; i++)
                if (_cachedSnapshot[i].PortraitOrder <= 0) _cachedSnapshot[i].PortraitOrder = i + 1;
        }

        private static List<BattlegroundsScryMemory.RailTile> OrderDuoRailTiles(List<BattlegroundsScryMemory.RailTile> rail)
        {
            return rail == null ? new List<BattlegroundsScryMemory.RailTile>() : rail.Take(MaxRows).ToList();
        }

        private List<LobbyHeroInfo> ReadDirectNativeRailSnapshot()
        {
            // Compatibility helper retained for diagnostics/history. Live V3 updates use
            // ReadLeaderboardTileForTeam() one flattened visual seat at a time (all cards within each team).
            var result = new List<LobbyHeroInfo>();
            try
            {
                var rail = BattlegroundsScryMemory.Instance.ReadLeaderboardTiles();
                if (rail == null || rail.Count == 0) return result;
                var byTeam = rail.Where(t => t != null && t.Team >= 0 && t.Team < MaxRows)
                    .GroupBy(t => t.Team).ToDictionary(g => g.Key, g => g.First());
                for (int order = 0; order < MaxRows; order++)
                {
                    BattlegroundsScryMemory.RailTile tile;
                    if (!byTeam.TryGetValue(order, out tile))
                    {
                        result.Add(new LobbyHeroInfo { PortraitOrder = order + 1, IsUnresolved = true });
                        continue;
                    }
                    string tribe = NormalizeLobbyTribe(tile.NativeTribe);
                    bool validTribe = IsKnownLobbyTribe(tribe);
                    result.Add(new LobbyHeroInfo
                    {
                        Id = tile.EntityId, Health = 1, TavernTier = tile.NativeTier > 0 ? tile.NativeTier : 0,
                        Tribe = validTribe ? tribe : "Neutral", TribeCount = (validTribe && tile.NativeCount >= 0 && tile.NativeCount <= 7) ? tile.NativeCount : 0,
                        PortraitOrder = order + 1, IsDead = false, IsUnresolved = !validTribe && tile.NativeTier <= 0
                    });
                }
            }
            catch { }
            return result;
        }

        private void ResetSnapshotState()
        {
            _modeLocked = false;
            _isDuoMode = false;
            _lastDuoOrderRefresh = DateTime.MinValue;
            for (int i = 0; i < MaxRows; i++) _duoVisualToNativeSeat[i] = i;
            _lastObservedTurn = -1;
            _snapshotRetryUntil = DateTime.MinValue;
            _nextSnapshotRetry = DateTime.MinValue;
            _snapshotRetryCount = 0;
            _lastNativeRailPoll = DateTime.MinValue;
            _nativeRailPollNotBefore = DateTime.MinValue;
            _lastNativeDataSignature = null;
            _lastNativeDataChange = DateTime.MinValue;
            _nativeRebindTurn = -1;
            _nativeRebindCountThisTurn = 0;
            _cachedSnapshot.Clear();
            _lastRenderSignature = null;
            _lastGameRect = Rect.Empty;
            _heroRuntimeCacheAt = DateTime.MinValue;
            _heroRuntimeCache.Clear();
            _runtimeSelfPlayerId = 0;
            _runtimeSelfHeroCardId = null;
            _tierBaselineByIdentity.Clear();
            _levelUpTurnByIdentity.Clear();
            _nativeSeatCursor = 0;
            for (int i = 0; i < MaxRows; i++)
            {
                _nativeSeatMissingStreak[i] = 0;
                _nativeSeatRetryAt[i] = DateTime.MinValue;
            }
        }

        private static int GetBattlegroundsTurnNumber(object game)
        {
            if (game == null) return 0;
            try
            {
                object value = PluginReflection.TryInvoke(game, "GetTurnNumber", null);
                if (value != null) return Math.Max(0, Convert.ToInt32(value));
            }
            catch { }
            return FirstPositive(
                PluginReflection.GetInt(game, "TurnNumber"),
                PluginReflection.GetInt(game, "CurrentTurn"),
                PluginReflection.GetInt(game, "Turn")
            );
        }

        public void SetPositionAbsolute(double left, double top)
        {
            Rect rect;
            IntPtr handle;
            if (!TryFindHearthstone(out rect, out handle)) return;

            ClampToGame(rect, ref left, ref top);
            Left = left;
            Top = top;

            Point anchor = GetRailAnchor(rect);
            _offsetXNorm = rect.Width > 1 ? (Left - anchor.X) / rect.Width : 0;
            _offsetYNorm = rect.Height > 1 ? (Top - anchor.Y) / rect.Height : 0;
            _hasCustomPosition = true;
            // Persist once on mouse-up. Do not write settings on every mouse move.
        }

        private void ApplyPosition(Rect rect, bool force)
        {
            // The default anchor is the left Battlegrounds rail area. It is kept in one
            // function so a future direct rail-memory anchor can replace it without touching
            // the persisted-offset/drag lifecycle.
            Point anchor = GetRailAnchor(rect);
            double left = anchor.X;
            double top = anchor.Y;
            if (_hasCustomPosition)
            {
                left += _offsetXNorm * rect.Width;
                top += _offsetYNorm * rect.Height;
            }
            if (!force && Math.Abs(Left - left) < 0.5 && Math.Abs(Top - top) < 0.5) return;
            double wantedLeft = left;
            double wantedTop = top;
            ClampToGame(rect, ref left, ref top);
            Left = left;
            Top = top;
            if (_hasCustomPosition && (Math.Abs(wantedLeft - left) > 0.5 || Math.Abs(wantedTop - top) > 0.5))
            {
                Point clampedAnchor = GetRailAnchor(rect);
                _offsetXNorm = rect.Width > 1 ? (Left - clampedAnchor.X) / rect.Width : 0;
                _offsetYNorm = rect.Height > 1 ? (Top - clampedAnchor.Y) / rect.Height : 0;
                SavePosition();
            }
        }

        private static Point GetRailAnchor(Rect rect)
        {
            return new Point(
                rect.Left + rect.Width * DefaultRailLeftRatio,
                rect.Top + rect.Height * DefaultRailTopRatio);
        }





        private static List<LobbyHeroInfo> BuildDisplaySnapshot(List<LobbyHeroInfo> source)
        {
            // Preserve native PlayerLeaderboardCards order exactly. V23's pair reversal was
            // incorrect: Hearthstone's native card order is the visual source of truth.
            // PlayerId/EntityId are identities only, never sort keys.
            return source == null ? new List<LobbyHeroInfo>() : source.ToList();
        }

        private string BuildRenderSignature(List<LobbyHeroInfo> infos, double rowHeight)
        {
            string core = string.Join("|", infos.Select(x => string.Join(":", x.Id, x.Health, x.TavernTier,
                NormalizeLobbyTribe(x.Tribe), x.TribeCount, x.PortraitOrder, x.LevelUpPending, x.PlayerId, x.DuoTeammatePlayerId, x.DuoFightsFirst )));
            return rowHeight.ToString("0.##") + ";" + core;
        }

        private void RenderRows(List<LobbyHeroInfo> infos, double rowHeight)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                LobbyHeroInfo info = i < infos.Count
                    ? infos[i]
                    : new LobbyHeroInfo { PortraitOrder = i + 1, IsUnresolved = true };
                row.Height = rowHeight;
                row.BorderBrush = new SolidColorBrush(info.IsSelf ? Color.FromRgb(0, 235, 210) : Color.FromArgb(170, 150, 95, 220));
                row.BorderThickness = new Thickness(info.IsSelf ? 2 : 1);
                row.Child = BuildRow(info);
                row.Visibility = Visibility.Visible;
            }
        }

        private void ClampToGame(Rect rect, ref double left, ref double top)
        {
            left = Math.Max(rect.Left, Math.Min(left, rect.Right - Width));
            top = Math.Max(rect.Top, Math.Min(top, rect.Bottom - Height));
        }

        private UIElement BuildRow(LobbyHeroInfo info)
        {
            if (info.IsDead)
            {
                var dead = new Grid();
                var deadText = new TextBlock
                {
                    Text = "×",
                    Foreground = new SolidColorBrush(Color.FromRgb(255, 90, 105)),
                    FontWeight = FontWeights.Bold,
                    FontSize = 22,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Player eliminated"
                };
                dead.Children.Add(deadText);
                return dead;
            }

            bool mixedTribe = string.Equals(info.Tribe, "Mixed", StringComparison.OrdinalIgnoreCase);
            string tribe = mixedTribe ? "Mixed" : NormalizeLobbyTribe(info.Tribe);
            int count = Math.Max(0, info.TribeCount);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Color accent = info.IsSelf ? Color.FromRgb(0, 235, 210) : Color.FromRgb(195, 120, 255);
            Color bg = info.IsSelf ? Color.FromArgb(220, 8, 52, 48) : Color.FromArgb(220, 42, 22, 68);
            var tier = new Border
            {
                Width = 30,
                Height = 24,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(accent),
                BorderThickness = new Thickness(info.IsSelf ? 2 : 1),
                VerticalAlignment = VerticalAlignment.Center
            };
            tier.Child = new TextBlock
            {
                Text = info.IsUnresolved ? "—" : (info.TavernTier > 0 ? "T" + info.TavernTier + (info.LevelUpPending ? " ↑" : string.Empty) : "T?"),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            Grid.SetColumn(tier, 0);
            grid.Children.Add(tier);

            ImageSource iconSource = (!info.IsUnresolved && !mixedTribe) ? GetTribeIcon(tribe) : null;
            if (mixedTribe)
            {
                var mixed = new TextBlock
                {
                    Text = "-",
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 15,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(mixed, 1);
                grid.Children.Add(mixed);
            }
            else if (iconSource != null)
            {
                var iconImage = new Image
                {
                    Source = iconSource,
                    Width = 26,
                    Height = 26,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = info.IsSelf ? "You • " + tribe + (count > 0 ? " ×" + count.ToString() : string.Empty) :
                        (info.TavernTier > 0 ? "Tavern Tier " + info.TavernTier + " • " + tribe + (count > 0 ? " ×" + count.ToString() : string.Empty) : tribe)
                };
                Grid.SetColumn(iconImage, 1);
                grid.Children.Add(iconImage);
            }
            else
            {
                var fallback = new TextBlock
                {
                    Text = info.IsUnresolved ? "·" : "•",
                    Foreground = new SolidColorBrush(TribeColor(tribe)),
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                Grid.SetColumn(fallback, 1);
                grid.Children.Add(fallback);
            }

            var countText = new TextBlock
            {
                Text = count > 0 ? count.ToString() : string.Empty,
                Foreground = new SolidColorBrush(info.IsSelf ? Color.FromRgb(90, 255, 235) : TribeColor(tribe)),
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = info.IsSelf ? "You • " + tribe + (count > 0 ? " ×" + count.ToString() : string.Empty) :
                    (info.TavernTier > 0 ? "Tavern Tier " + info.TavernTier + " • " + tribe + (count > 0 ? " ×" + count.ToString() : string.Empty) : tribe)
            };
            Grid.SetColumn(countText, 2);
            grid.Children.Add(countText);
            return grid;
        }

        private static string NormalizeLobbyTribe(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Neutral";
            string s = value.Trim();
            if (s.StartsWith("RACE_", StringComparison.OrdinalIgnoreCase)) s = s.Substring(5);
            s = s.Replace("_", string.Empty).Replace("-", string.Empty);
            if (s.Equals("MECHANICAL", StringComparison.OrdinalIgnoreCase)) return "Mech";
            if (s.Equals("ELEMENTALS", StringComparison.OrdinalIgnoreCase) || s.Equals("ELEMENTAL", StringComparison.OrdinalIgnoreCase)) return "Elemental";
            if (s.Equals("QUILLBOAR", StringComparison.OrdinalIgnoreCase) || s.Equals("QUILLBOARS", StringComparison.OrdinalIgnoreCase)) return "Quilboar";
            if (s.Equals("NONE", StringComparison.OrdinalIgnoreCase) || s.Equals("INVALID", StringComparison.OrdinalIgnoreCase)) return "Neutral";
            var aliases = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            {
                { "BEASTS", "Beast" }, { "DEMONS", "Demon" }, { "DRAGONS", "Dragon" },
                { "MECHS", "Mech" }, { "MECHANICALS", "Mech" }, { "MURLOCS", "Murloc" },
                { "NAGAS", "Naga" }, { "PIRATES", "Pirate" }, { "UNDEADS", "Undead" }
            };
            string alias;
            if (aliases.TryGetValue(s, out alias)) return alias;
            foreach (string k in new[] { "Beast", "Demon", "Dragon", "Elemental", "Mech", "Murloc", "Naga", "Pirate", "Quilboar", "Undead" })
                if (s.Equals(k, StringComparison.OrdinalIgnoreCase)) return k;
            return s;
        }

        private static readonly Dictionary<string, ImageSource> TribeIconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly object TribeIconCacheLock = new object();

        internal static ImageSource GetTribeIcon(string tribe)
        {
            string key = NormalizeLobbyTribe(tribe);
            if (string.IsNullOrWhiteSpace(key) || key.Equals("Neutral", StringComparison.OrdinalIgnoreCase)) return null;

            lock (TribeIconCacheLock)
            {
                ImageSource cached;
                if (TribeIconCache.TryGetValue(key, out cached)) return cached;
            }

            string fileName = key + ".png";
            string assemblyDir = IOPath.GetDirectoryName(typeof(ShopWishlistPlugin).Assembly.Location) ?? string.Empty;
            string path = IOPath.Combine(assemblyDir, "Assets", "TribeIcons", fileName);
            if (!File.Exists(path)) return null;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.DecodePixelWidth = 64;
                bitmap.EndInit();
                bitmap.Freeze();
                lock (TribeIconCacheLock) TribeIconCache[key] = bitmap;
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private static Color TribeColor(string tribe)
        {
            switch ((tribe ?? string.Empty).ToUpperInvariant())
            {
                case "BEAST": return Color.FromRgb(110, 235, 135);
                case "DEMON": return Color.FromRgb(255, 95, 160);
                case "DRAGON": return Color.FromRgb(255, 140, 105);
                case "ELEMENTAL": return Color.FromRgb(75, 215, 255);
                case "MECH": return Color.FromRgb(255, 85, 85);
                case "MURLOC": return Color.FromRgb(60, 225, 195);
                case "NAGA": return Color.FromRgb(95, 155, 255);
                case "PIRATE": return Color.FromRgb(255, 190, 80);
                case "QUILBOAR": return Color.FromRgb(255, 150, 75);
                case "UNDEAD": return Color.FromRgb(190, 120, 255);
                default: return Color.FromRgb(205, 190, 220);
            }
        }

        private void LoadPosition()
        {
            if (_positionLoaded) return;
            _positionLoaded = true;
            try
            {
                string dir = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HDT-Shop-Wishlist");
                string file = IOPath.Combine(dir, PositionFileName);
                if (!File.Exists(file)) return;
                string[] parts = File.ReadAllText(file).Trim().Split('|');
                if (parts.Length != 3 || !string.Equals(parts[0], "v2", StringComparison.OrdinalIgnoreCase)) return;
                double x, y;
                if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x)) return;
                if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y)) return;
                if (Math.Abs(x) > 2.0 || Math.Abs(y) > 2.0) return;
                _offsetXNorm = x;
                _offsetYNorm = y;
                _hasCustomPosition = Math.Abs(x) > 0.0001 || Math.Abs(y) > 0.0001;
            }
            catch { }
        }

        private void SavePosition()
        {
            if (!_positionLoaded || !_hasCustomPosition) return;
            try
            {
                string dir = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HDT-Shop-Wishlist");
                Directory.CreateDirectory(dir);
                string file = IOPath.Combine(dir, PositionFileName);
                File.WriteAllText(file,
                    "v2|" + _offsetXNorm.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                    "|" + _offsetYNorm.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
            catch { }
        }

        private static int FirstPositive(params int[] values) { foreach (int v in values) if (v > 0) return v; return 0; }
        private static string FirstText(params string[] values) { foreach (string v in values) if (!string.IsNullOrWhiteSpace(v)) return v; return null; }
        private static bool TryFindHearthstone(out Rect rect, out IntPtr handle)
        {
            Native.RECT r;
            if (Native.TryFindHearthstoneWindow(out r, out handle)) { rect = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top); return true; }
            rect = Rect.Empty; handle = IntPtr.Zero;
            return false;
        }
        private void HideAll()
        {
            foreach (Border row in _rows)
            {
                row.Visibility = Visibility.Collapsed;
                row.Child = null;
            }
        }
        private const string SkipCombatFirewallRuleName = "HDTShopWishlist_SkipCombat_Temp";
        // How long the outbound block is held, and how long any single netsh call may take.
        // The hold is THE lever on the observed failure: too short and the client never notices
        // the drop, so nothing is skipped; too long and the server tears the session down instead
        // of letting the client resume, which is when the game dies. 2000ms is the measured value:
        // 8 runs at 2000 all survived, against 2 client deaths in 5 runs at the original 3000.
        // It is still read from a file at each click and can be retuned
        // between two games without rebuilding or reinstalling anything.
        private const int SkipCombatBlockMsDefault = 2000;
        private const int SkipCombatNetshTimeoutMs = 3000;
        // How long the game is watched after the unblock. Both observed deaths happened 15-19s
        // AFTER the block was lifted, never during it, so a short window cannot tell a good run
        // from a bad one.
        private const int SkipCombatWatchMs = 30000;

        internal static string SkipCombatBlockMsPath
        {
            get
            {
                return IOPath.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "HDTShopWishlist", "skip-combat-ms.txt");
            }
        }

        private static int ReadSkipCombatBlockMs()
        {
            try
            {
                string p = SkipCombatBlockMsPath;
                int v;
                if (File.Exists(p) && int.TryParse(File.ReadAllText(p).Trim(), out v) && v >= 200 && v <= 10000)
                    return v;
            }
            catch { }
            return SkipCombatBlockMsDefault;
        }

        // Labels each run in the log by itself, so a tuning session does not depend on anyone
        // remembering which try crashed.
        private static void MonitorOutcome(string runId, int pid, int blockMs)
        {
            if (pid <= 0) return;
            Task.Run(async delegate
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    while (sw.ElapsedMilliseconds < SkipCombatWatchMs)
                    {
                        await Task.Delay(2000);
                        bool alive;
                        try { using (Process p = Process.GetProcessById(pid)) alive = !p.HasExited; }
                        catch { alive = false; }
                        if (!alive)
                        {
                            SkipLog("### OUTCOME " + runId + " = FAILED - Hearthstone (pid " + pid + ") exited "
                                + sw.ElapsedMilliseconds + "ms after the unblock | blockMs=" + blockMs);
                            return;
                        }
                    }
                    SkipLog("### OUTCOME " + runId + " = OK - still alive " + SkipCombatWatchMs + "ms after the unblock | blockMs=" + blockMs);
                }
                catch (Exception ex) { SkipLog("### OUTCOME " + runId + " = UNKNOWN (" + ex.GetType().Name + ")"); }
            });
        }
        private bool _skipCombatBusy;
        private async void SkipCombatButtonClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true; // don't let this also start a window drag
            if (_skipCombatBusy) { SkipLog("CLICK ignored - a run is already in progress"); return; }
            _skipCombatBusy = true;
            string runId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var total = Stopwatch.StartNew();
            try
            {
                Process proc = Process.GetProcessesByName("Hearthstone").FirstOrDefault();
                string exePath = null;
                string pathError = null;
                try { exePath = proc != null ? proc.MainModule.FileName : null; }
                catch (Exception ex) { pathError = ex.GetType().Name + ": " + ex.Message; }

                int hsPid = proc != null ? proc.Id : 0;
                int blockMs = ReadSkipCombatBlockMs();

                SkipLog("=== CLICK " + runId + " === blockMs=" + blockMs
                    + (blockMs == SkipCombatBlockMsDefault ? " (default)" : " (from skip-combat-ms.txt)")
                    + " elevated=" + IsProcessElevated()
                    + " hearthstone=" + (proc == null ? "NOT RUNNING" : "pid " + proc.Id)
                    + " exePath=" + (string.IsNullOrWhiteSpace(exePath)
                        ? "<none>" + (pathError != null ? " (" + pathError + ")" : string.Empty)
                        : exePath));

                if (string.IsNullOrWhiteSpace(exePath))
                {
                    SkipLog("  ABORT - no Hearthstone executable path, nothing was blocked");
                }
                else
                {
                    // Real network cut (not a process freeze): a temporary Windows Firewall rule
                    // blocking Hearthstone.exe's traffic. The game keeps rendering/responding
                    // normally; it just can't reach the BG server for a few seconds, exactly like a
                    // real dropped connection - which is what makes the client skip straight to the
                    // shop instead of replaying combat once it reconnects.
                    // Outbound-only, and shorter: blocking dir=in as well can force an abrupt reset
                    // of an already-established TCP connection instead of a graceful timeout, which
                    // is the likely cause of an observed game hang/crash with the previous version.
                    // Each RunNetsh call blocks synchronously on its own (WaitForExit); run the whole
                    // sequence on a background thread so a slow/hung netsh can never freeze the UI.
                    bool blockAdded = false;
                    var held = new Stopwatch();
                    await Task.Run(delegate { LogConnectionSample("before-add"); });
                    await Task.Run(delegate
                    {
                        LoggedNetsh("pre-delete", "advfirewall firewall delete rule name=\"" + SkipCombatFirewallRuleName + "\"");
                        NetshResult add = LoggedNetsh("add-block", "advfirewall firewall add rule name=\"" + SkipCombatFirewallRuleName + "\" dir=out program=\"" + exePath + "\" action=block enable=yes");
                        blockAdded = add.Started && !add.TimedOut && add.ExitCode == 0;
                        held.Start();
                    });
                    SkipLog("  block installed=" + blockAdded + " - holding for " + blockMs + "ms");
                    // Sampled on a background thread so the measured hold stays honest.
                    Task.Run(async delegate { await Task.Delay(Math.Min(700, blockMs / 2)); LogConnectionSample("during-block"); });
                    try { await Task.Delay(blockMs); }
                    finally
                    {
                        await Task.Run(delegate
                        {
                            LoggedNetsh("unblock", "advfirewall firewall delete rule name=\"" + SkipCombatFirewallRuleName + "\"");
                            held.Stop();
                            // The real held duration, not the nominal one: netsh calls and thread
                            // scheduling both add to it, and that overshoot is the difference
                            // between landing in the shop and dropping the session.
                            SkipLog("  block held for " + held.ElapsedMilliseconds + "ms actual (nominal " + blockMs + ")");
                            string detail;
                            SkipLog(SkipCombatRuleExists(out detail)
                                ? "  !! RULE STILL PRESENT after unblock - Hearthstone stays firewalled. " + detail
                                : "  rule confirmed removed");
                            LogConnectionSample("after-unblock");
                        });
                        // Fire and forget from here: the button must be usable again as soon as
                        // the block is lifted. Awaiting the tail samples kept it locked for ~6s
                        // instead of ~3.5s, which is a long time to be stuck mid-game.
                        Task.Run(async delegate { await Task.Delay(2500); LogConnectionSample("post+2.5s"); });
                        MonitorOutcome(runId, hsPid, blockMs);
                    }
                }
            }
            catch (Exception ex) { SkipLog("  EXCEPTION " + ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                _skipCombatBusy = false;
                SkipLog("=== END " + runId + " === " + total.ElapsedMilliseconds + "ms total");
            }
        }

        // ---- Skip Combat diagnostics -------------------------------------------------------
        // The failure being chased is intermittent and, by design, happens while the game's
        // network is cut - so the log must survive a disconnect, a hang, or HDT being killed
        // outright. Every line is appended and closed immediately; nothing is allowed to sit in
        // a buffer waiting for a graceful shutdown that may never come.
        internal static readonly string SkipCombatLogPath = IOPath.Combine(IOPath.GetTempPath(), "hdt_skipcombat.log");
        private static readonly object SkipCombatLogLock = new object();

        internal static void SkipLog(string line)
        {
            try
            {
                lock (SkipCombatLogLock)
                {
                    File.AppendAllText(SkipCombatLogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + line + Environment.NewLine);
                }
            }
            catch { }
        }

        private sealed class NetshResult
        {
            public bool Started;
            public bool TimedOut;
            public int ExitCode = -1;
            public long ElapsedMs;
            public string Output = string.Empty;
            public string Error = string.Empty;
        }

        private static string Flatten(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            string one = s.Replace("\r", " ").Replace("\n", " / ").Trim();
            while (one.Contains("  ")) one = one.Replace("  ", " ");
            return one.Length > 300 ? one.Substring(0, 300) + "..." : one;
        }

        private static NetshResult RunNetsh(string arguments)
        {
            var r = new NetshResult();
            var sw = Stopwatch.StartNew();
            try
            {
                var psi = new ProcessStartInfo("netsh", arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    r.Started = true;
                    // Drain both pipes before waiting: a full pipe buffer would deadlock
                    // WaitForExit, which would look exactly like the "netsh hung" case we are
                    // trying to tell apart from a genuine failure.
                    string so = p.StandardOutput.ReadToEnd();
                    string se = p.StandardError.ReadToEnd();
                    r.TimedOut = !p.WaitForExit(SkipCombatNetshTimeoutMs);
                    r.Output = (so ?? string.Empty).Trim();
                    r.Error = (se ?? string.Empty).Trim();
                    if (!r.TimedOut) { try { r.ExitCode = p.ExitCode; } catch { } }
                }
            }
            catch (Exception ex) { r.Error = ex.GetType().Name + ": " + ex.Message; }
            r.ElapsedMs = sw.ElapsedMilliseconds;
            return r;
        }

        private static NetshResult LoggedNetsh(string label, string arguments)
        {
            NetshResult r = RunNetsh(arguments);
            SkipLog(string.Format("  {0,-12} exit={1} timedOut={2} {3}ms{4}{5}",
                label,
                r.Started ? r.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NOT-STARTED",
                r.TimedOut, r.ElapsedMs,
                r.Output.Length > 0 ? " | out: " + Flatten(r.Output) : string.Empty,
                r.Error.Length > 0 ? " | ERR: " + Flatten(r.Error) : string.Empty));
            return r;
        }

        // netsh exits 0 and prints the rule when it exists; exits non-zero with "No rules match"
        // when it does not. This is the check that tells an orphaned block apart from a clean run.
        private static bool SkipCombatRuleExists(out string detail)
        {
            NetshResult r = RunNetsh("advfirewall firewall show rule name=\"" + SkipCombatFirewallRuleName + "\"");
            detail = "exit=" + r.ExitCode + " out: " + Flatten(r.Output) + (r.Error.Length > 0 ? " ERR: " + Flatten(r.Error) : string.Empty);
            return r.Started && !r.TimedOut && r.ExitCode == 0;
        }

        // Processes whose live TCP connections are counted around each block. The question being
        // answered is narrow: does anything OTHER than Hearthstone lose its connections at the
        // exact moment the firewall rule is added or removed? A program-scoped outbound rule
        // cannot block another process by design, so if Discord's count drops here, the cause is
        // the policy change itself disturbing established flows, not the rule's scope.
        private static readonly string[] SkipCombatWatchedProcesses = { "Hearthstone", "Discord", "HearthstoneDeckTracker" };

        private static void LogConnectionSample(string label)
        {
            try
            {
                // netstat rather than a GetExtendedTcpTable interop: no elevation, no P/Invoke,
                // and per-process counts are all this needs to prove or disprove the theory.
                var psi = new ProcessStartInfo("netstat", "-ano")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                string text;
                using (Process p = Process.Start(psi))
                {
                    text = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(4000);
                }

                var perPid = new Dictionary<int, int>();
                int total = 0;
                foreach (string raw in text.Split('\n'))
                {
                    string line = raw.Trim();
                    if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase)) continue;
                    string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 4) continue;
                    // State names are localised, so filter on the remote endpoint instead: a
                    // listening socket has no peer. Locale-proof.
                    string remote = parts[2];
                    if (remote.EndsWith(":0") || remote.EndsWith("]:0")) continue;
                    int pid;
                    if (!int.TryParse(parts[parts.Length - 1], out pid)) continue;
                    int c; perPid.TryGetValue(pid, out c); perPid[pid] = c + 1;
                    total++;
                }

                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (string n in SkipCombatWatchedProcesses) counts[n] = 0;
                foreach (var kv in perPid)
                {
                    string name = null;
                    try { using (Process pr = Process.GetProcessById(kv.Key)) name = pr.ProcessName; } catch { }
                    if (name == null) continue;
                    foreach (string n in SkipCombatWatchedProcesses)
                        if (name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) counts[n] += kv.Value;
                }

                var sb = new StringBuilder();
                foreach (string n in SkipCombatWatchedProcesses)
                    sb.Append(n).Append('=').Append(counts[n]).Append("  ");
                SkipLog("  conn " + label.PadRight(13) + sb + "allPeered=" + total);
            }
            catch (Exception ex) { SkipLog("  conn " + label + " sample FAILED: " + ex.GetType().Name + ": " + ex.Message); }
        }

        internal static bool IsProcessElevated()
        {
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // Called once at plugin load. An orphaned rule found here means a previous Skip Combat
        // never got to remove its block - HDT was closed, killed or crashed inside the hold
        // window - and Hearthstone has been firewalled ever since. That is the single most
        // likely explanation for "it disconnects me and I can't dismiss the reconnect prompt".
        internal static void LogSkipCombatStartupState()
        {
            try
            {
                SkipLog("===== HDT START ===== elevated=" + IsProcessElevated());
                string detail;
                if (SkipCombatRuleExists(out detail))
                {
                    SkipLog("  !! ORPHANED BLOCK RULE FOUND AT STARTUP - Hearthstone was still firewalled. " + detail);
                    LoggedNetsh("orphan-del", "advfirewall firewall delete rule name=\"" + SkipCombatFirewallRuleName + "\"");
                    SkipLog(SkipCombatRuleExists(out detail) ? "  !! orphan removal FAILED. " + detail : "  orphan removed");
                }
                else
                {
                    SkipLog("  no leftover rule");
                }
            }
            catch { }
        }

        internal static void CleanupSkipCombatRule(string reason)
        {
            try
            {
                string detail;
                if (!SkipCombatRuleExists(out detail)) return;
                SkipLog("CLEANUP (" + reason + ") - block rule still present, removing. " + detail);
                LoggedNetsh("cleanup-del", "advfirewall firewall delete rule name=\"" + SkipCombatFirewallRuleName + "\"");
                SkipLog(SkipCombatRuleExists(out detail) ? "  !! cleanup FAILED. " + detail : "  cleanup ok");
            }
            catch { }
        }

        private void ApplyNoActivate()
        {
            // The panel must stay clickable (for dragging) without ever becoming the OS foreground/
            // active window - otherwise clicking it flips Native.IsForegroundHearthstone() to false
            // mid-drag, which made UpdateForCurrentGame() hide the whole panel every time a drag
            // started (the invisible/flickery/"stops following" symptom).
            try
            {
                var src = PresentationSource.FromVisual(this) as HwndSource;
                if (src == null) return;
                IntPtr h = src.Handle;
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            }
            catch { }
        }

        private void HandleMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            _dragging = true;
            _dragStartScreen = PointToScreen(e.GetPosition(this));
            _dragStartLeft = Left;
            _dragStartTop = Top;
            CaptureMouse();
            e.Handled = true;
        }

        private void HandleMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            if (e.LeftButton != MouseButtonState.Pressed) { FinishDrag(); return; }
            Point now = PointToScreen(e.GetPosition(this));
            double dx = now.X - _dragStartScreen.X;
            double dy = now.Y - _dragStartScreen.Y;
            SetPositionAbsolute(_dragStartLeft + dx, _dragStartTop + dy);
            e.Handled = true;
        }

        private void HandleMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            FinishDrag();
            e.Handled = true;
        }

        private void HandleLostMouseCapture(object sender, MouseEventArgs e)
        {
            // Capture was revoked without a matching HandleMouseUp - clear the stuck flag but
            // skip SavePosition(): the drag never reached a deliberate mouse-up, so the current
            // Left/Top may not reflect where the user actually meant to drop the panel.
            _dragging = false;
        }

        private void FinishDrag()
        {
            if (!_dragging) return;
            _dragging = false;
            try { ReleaseMouseCapture(); } catch { }
            SavePosition();
        }

        private static bool IsKnownLobbyTribe(string tribe)
        {
            string s = NormalizeLobbyTribe(tribe);
            return new[] { "Beast", "Demon", "Dragon", "Elemental", "Mech", "Murloc", "Naga", "Pirate", "Quilboar", "Undead" }
                .Any(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal sealed class WishlistWindow : Window
    {
        private readonly WishlistStore _store; private readonly Action _onSaved; private int _editingComp;
        private readonly WrapPanel _library = new WrapPanel(); private readonly StackPanel _selected = new StackPanel();
        private readonly StackPanel _tribeRow = new StackPanel();
        private readonly List<string> _tribeOrder = new List<string>();
        private Button _draggedTribeButton;
        private Point _dragStart;
        private bool _draggingTribe;
        private readonly Button[] _tierButtons = new Button[8];
        private readonly Dictionary<string, Button> _tribeButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        private int _tierFilter = 1;
        private string _categoryFilter = "Minions";
        private string _tribeFilter = "All Tribes";
        private int _selectedPriorityFilter = 0; // 0=all, 1=core, 2=important, 3=optional
        private TextBox _searchBox;
        private string _searchQuery = string.Empty;
        private int _libraryRefreshGeneration;
        private readonly StackPanel _selectedFilterRow = new StackPanel();
        private HashSet<string> _lobbyAvailableTribes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _lobbyTribeFilterKnown;
        private readonly Button[] _compTabs = new Button[WishlistStore.MaxComps];
        private StackPanel _compTabsPanel;
        private Button _addCompButton;
        private readonly Dictionary<string, Button> _categoryButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        private readonly StackPanel _categoryRow = new StackPanel();
        private readonly TextBlock _status = new TextBlock();
        private Border _poolInfoPanel;
        private Button _poolInfoButton;
        private Border _inGameActiveBadge;
        private TextBlock _windowTitleText;
        private TextBlock _selectedCountText;
        private Button _setActiveButton;
        private List<CardDescriptor> _allCards = new List<CardDescriptor>();
        private List<CardDescriptor> _poolCards = new List<CardDescriptor>();
        private DispatcherTimer _libraryRefreshDebounceTimer;
        private bool _libraryRefreshDirty;
        private const string AllTribe = "All Tribes";
        private const string RemoteImageBaseUrl = "https://hsbg.cards";
        private const string RemoteArtOnlyBaseUrl = "https://art.hearthstonejson.com/v1/512x/";
        private static readonly string[] DefaultTribes = new[] { "Beast", "Demon", "Dragon", "Elemental", "Mech", "Murloc", "Naga", "Pirate", "Quilboar", "Undead" };
        public bool IsInGameMode { get; private set; }
        private DateTime _suppressDeactivateUntil = DateTime.MinValue;
        private bool _windowDragging;
        private bool _windowDragMoved;
        private Point _windowDragStartScreen;
        private double _windowDragStartLeft;
        private double _windowDragStartTop;
        private DateTime _lastWindowDragMove = DateTime.MinValue;

        public void SuppressDeactivateFor(int milliseconds)
        {
            _suppressDeactivateUntil = DateTime.UtcNow.AddMilliseconds(Math.Max(0, milliseconds));
        }

        public WishlistWindow(WishlistStore store, Action onSaved)
        {
            _store=store; _onSaved=onSaved; _editingComp=store.ActiveCompIndex;
            Title="Battlegrounds Comp Builder"; Width=1280; Height=850; WindowStartupLocation=WindowStartupLocation.CenterScreen;
            WindowStyle=WindowStyle.None; AllowsTransparency=true; ShowInTaskbar=false;
            Background=new SolidColorBrush(Color.FromRgb(20,14,30)); Foreground=Brushes.White;
            TrySetWindowIcon();
            BuildUi();
            UpdateTribeRowVisibility();
            UpdateFilterButtons();
            LoadCardsAsync();
            Deactivated += delegate
            {
                if (!IsInGameMode || Visibility != Visibility.Visible) return;
                if (DateTime.UtcNow < _suppressDeactivateUntil) return;
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (DateTime.UtcNow < _suppressDeactivateUntil) return;
                    if (!IsInGameMode || Visibility != Visibility.Visible || IsActive) return;
                    // In-game builder is deliberately click/focus scoped: if this window loses
                    // focus (including Alt+Tab), hide it. The launcher remains available.
                    Hide();
                }), DispatcherPriority.Background);
            };
        }

        public void PrepareInGame(Rect gameRect)
        {
            IsInGameMode = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = true;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.None;
            Background = new SolidColorBrush(Color.FromArgb(248, 17, 11, 28));
            // Compact in-game layout: keep the desktop builder larger, but make the live panel
            // feel like a tactical HUD rather than a full desktop window.
            Width = Math.Min(1380, Math.Max(1180, gameRect.Width - 120));
            Height = Math.Min(820, Math.Max(680, gameRect.Height - 100));
            Left = Math.Round(gameRect.Left + (gameRect.Width - Width) / 2.0);
            Top = Math.Round(gameRect.Top + (gameRect.Height - Height) / 2.0);
            if (_windowTitleText != null)
                _windowTitleText.Text = "BG COMP BUILDER  •  " + _store.GetCompName(_editingComp);
            if (_inGameActiveBadge != null)
                _inGameActiveBadge.Visibility = Visibility.Visible;
            RefreshLobbyTribes(HDTCore.Game);
            UpdateTabs();
            RefreshSelected();
        }

        private void TrySetWindowIcon()
        {
            try
            {
                string assemblyDir = IOPath.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = IOPath.Combine(assemblyDir ?? AppDomain.CurrentDomain.BaseDirectory, "Assets", "BGCompBuilderIcon.png");
                if (File.Exists(path))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    Icon = bmp;
                }
            }
            catch { }
        }

        private void WindowHeaderMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsInGameMode || e.ChangedButton != MouseButton.Left) return;
            if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;
            _windowDragging = true;
            _windowDragMoved = false;
            _windowDragStartScreen = PointToScreen(e.GetPosition(this));
            _windowDragStartLeft = Left;
            _windowDragStartTop = Top;
            _lastWindowDragMove = DateTime.MinValue;
            try { Mouse.Capture(this); } catch { }
            e.Handled = true;
        }

        private void WindowHeaderMouseMove(object sender, MouseEventArgs e)
        {
            if (!_windowDragging) return;
            if (e.LeftButton != MouseButtonState.Pressed) { WindowHeaderMouseUp(sender, null); return; }
            Point now = PointToScreen(e.GetPosition(this));
            double dx = now.X - _windowDragStartScreen.X;
            double dy = now.Y - _windowDragStartScreen.Y;
            if (!_windowDragMoved && Math.Abs(dx) + Math.Abs(dy) < 4) return;
            _windowDragMoved = true;
            if ((DateTime.UtcNow - _lastWindowDragMove).TotalMilliseconds < 16) return;
            _lastWindowDragMove = DateTime.UtcNow;
            Left = Math.Round(_windowDragStartLeft + dx);
            Top = Math.Round(_windowDragStartTop + dy);
            e.Handled = true;
        }

        private void WindowHeaderMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_windowDragging) return;
            if (e != null && e.ChangedButton != MouseButton.Left) return;
            try { Mouse.Capture(null); } catch { }
            _windowDragging = false;
            _windowDragMoved = false;
            if (e != null) e.Handled = true;
        }

        private static bool TryFindHearthstone(out Rect rect, out IntPtr handle)
        {
            Native.RECT r;
            if (Native.TryFindHearthstoneWindow(out r, out handle)) { rect = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top); return true; }
            rect = Rect.Empty; handle = IntPtr.Zero;
            return false;
        }

        private void BuildUi()
        {
            var root=new Grid{Margin=new Thickness(14)};
            root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
            root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            var titleRow=new Grid{Margin=new Thickness(0,0,0,8),Cursor=Cursors.SizeAll};
            titleRow.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
            titleRow.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            titleRow.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            _windowTitleText=new TextBlock{Text="Battlegrounds Comp Builder",FontSize=25,FontWeight=FontWeights.Bold,Foreground=new SolidColorBrush(Color.FromRgb(214,125,255)),VerticalAlignment=VerticalAlignment.Center};
            Grid.SetColumn(_windowTitleText,0); titleRow.Children.Add(_windowTitleText);
            _inGameActiveBadge=new Border{Visibility=Visibility.Collapsed,Background=new SolidColorBrush(Color.FromArgb(235,42,120,88)),BorderBrush=new SolidColorBrush(Color.FromRgb(40,235,170)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Padding=new Thickness(6,2,6,2),Margin=new Thickness(8,0,0,0),VerticalAlignment=VerticalAlignment.Center};
            _inGameActiveBadge.Child=new TextBlock{Text="ACTIVE IN GAME",Foreground=new SolidColorBrush(Color.FromRgb(150,255,225)),FontWeight=FontWeights.Bold,FontSize=10};
            Grid.SetColumn(_inGameActiveBadge,1); titleRow.Children.Add(_inGameActiveBadge);
            titleRow.PreviewMouseLeftButtonDown += WindowHeaderMouseDown;
            titleRow.PreviewMouseMove += WindowHeaderMouseMove;
            titleRow.PreviewMouseLeftButtonUp += WindowHeaderMouseUp;
            var inGameClose=new Button{Content="×",Width=28,Height=28,Margin=new Thickness(8,0,0,0),Padding=new Thickness(0),Background=new SolidColorBrush(Color.FromArgb(150,40,24,54)),Foreground=new SolidColorBrush(Color.FromRgb(245,210,255)),BorderBrush=new SolidColorBrush(Color.FromRgb(166,44,128)),BorderThickness=new Thickness(1),ToolTip="Close in-game builder"};
            inGameClose.Click+=delegate{if(IsInGameMode){Hide();_inGameActiveBadge.Visibility=Visibility.Collapsed;}};
            Grid.SetColumn(inGameClose,2); titleRow.Children.Add(inGameClose);
            titleRow.ToolTip = "Drag header to move";
            Grid.SetRow(titleRow,0); root.Children.Add(titleRow);
            var tabs=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,10)};
            _compTabsPanel=new StackPanel{Orientation=Orientation.Horizontal};
            tabs.Children.Add(_compTabsPanel);
            _addCompButton=new Button{Content="+",Width=34,Height=30,Margin=new Thickness(6,0,0,0),Background=new SolidColorBrush(Color.FromRgb(55,38,76)),Foreground=new SolidColorBrush(Color.FromRgb(195,120,255)),BorderBrush=new SolidColorBrush(Color.FromRgb(195,120,255)),ToolTip="Add a new comp"};
            _addCompButton.Click+=delegate{AddComp();};
            tabs.Children.Add(_addCompButton);
            _setActiveButton=new Button{Content="SET ACTIVE IN GAME",Padding=new Thickness(14,8,14,8),Margin=new Thickness(20,0,0,0),Background=new SolidColorBrush(Color.FromRgb(98,54,140)),Foreground=Brushes.White,BorderBrush=new SolidColorBrush(Color.FromRgb(170,92,230)),BorderThickness=new Thickness(1),ToolTip="Make this comp the active in-game comp"};
            _setActiveButton.Click+=delegate{_store.SetActiveComp(_editingComp);UpdateTabs();UpdateActiveButton();SetStatus(_store.GetCompName(_editingComp)+" is now active in-game.");};
            tabs.Children.Add(_setActiveButton);
            Grid.SetRow(tabs,1);root.Children.Add(tabs);

            var body=new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(3.15,GridUnitType.Star)}); body.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1.45,GridUnitType.Star)}); Grid.SetRow(body,2);root.Children.Add(body);
            var left=new Grid(); left.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto}); left.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto}); left.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto}); left.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto}); left.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto}); left.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
            var filtersTop=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
            left.Children.Add(filtersTop); Grid.SetRow(filtersTop,0);

            _categoryRow.Orientation=Orientation.Horizontal; _categoryRow.Margin=new Thickness(0,7,0,2);
            AddCategoryButton("Minions", "MINIONS", Color.FromRgb(195,120,255));
            AddCategoryButton("Tavern Spells", "TAVERN SPELLS", Color.FromRgb(255,170,75));
            AddCategoryButton("Buddies", "BUDDIES", Color.FromRgb(95,210,255));
            AddCategoryButton("Battlecry", "BATTLECRY", Color.FromRgb(255,196,64));
            AddCategoryButton("Deathrattle", "DEATHRATTLE", Color.FromRgb(90,200,150));
            left.Children.Add(_categoryRow); Grid.SetRow(_categoryRow,1);

            var tierRow=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,7,0,4)};
            for(int t=0;t<=7;t++)
            {
                int captured=t; string label=t==0?"ALL":"T"+t;
                Button tierButton=new Button{Content=label,Width=t==0?52:42,Height=32,Margin=new Thickness(t==0?0:4,0,0,0),Padding=new Thickness(3,0,3,0),Background=new SolidColorBrush(Color.FromRgb(48,34,64)),Foreground=Brushes.White,BorderBrush=new SolidColorBrush(Color.FromRgb(112,74,140)),BorderThickness=new Thickness(1),ToolTip=t==0?"Show all current Tavern Tiers":"Show current Tavern Tier "+t};
                tierButton.Click+=delegate{SetTierFilter(captured);};
                _tierButtons[t]=tierButton; tierRow.Children.Add(tierButton);
            }
            left.Children.Add(tierRow); Grid.SetRow(tierRow,2);

            _tribeRow.Orientation=Orientation.Horizontal; _tribeRow.VerticalAlignment=VerticalAlignment.Center; _tribeRow.Margin=new Thickness(0,0,0,7);
            _tribeOrder.Clear();
            _tribeOrder.AddRange(_store.LoadTribeOrder(DefaultTribes));
            AddTribeButton(AllTribe, "ALL", Color.FromRgb(195,120,255));
            foreach(string tribe in _tribeOrder) AddTribeButton(tribe, TribeShortLabel(tribe), TribeColor(tribe));

            left.Children.Add(_tribeRow); Grid.SetRow(_tribeRow,3);

            var searchBorder=new Border{Margin=new Thickness(0,0,0,8),CornerRadius=new CornerRadius(6),Background=new SolidColorBrush(Color.FromRgb(31,22,42)),BorderBrush=new SolidColorBrush(Color.FromRgb(112,74,140)),BorderThickness=new Thickness(1)};
            var searchGrid=new Grid();
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
            var searchIcon=new TextBlock{Text="🔍",FontSize=12,Foreground=new SolidColorBrush(Color.FromRgb(170,140,195)),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(10,0,4,0)};
            Grid.SetColumn(searchIcon,0); searchGrid.Children.Add(searchIcon);
            _searchBox=new TextBox{Background=Brushes.Transparent,BorderThickness=new Thickness(0),Foreground=Brushes.White,CaretBrush=Brushes.White,FontSize=13,Padding=new Thickness(0,7,10,7),VerticalContentAlignment=VerticalAlignment.Center};
            var searchPlaceholder=new TextBlock{Text="Search by name, effect or keyword…",FontSize=13,Foreground=new SolidColorBrush(Color.FromRgb(130,110,150)),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(2,0,0,0),IsHitTestVisible=false};
            _searchBox.TextChanged+=delegate{_searchQuery=(_searchBox.Text??string.Empty).Trim();searchPlaceholder.Visibility=_searchQuery.Length==0?Visibility.Visible:Visibility.Collapsed;RefreshLibrary();};
            Grid.SetColumn(searchPlaceholder,1); searchGrid.Children.Add(searchPlaceholder);
            Grid.SetColumn(_searchBox,1); searchGrid.Children.Add(_searchBox);
            searchBorder.Child=searchGrid;
            left.Children.Add(searchBorder); Grid.SetRow(searchBorder,4);

            var scroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=new SolidColorBrush(Color.FromRgb(31,22,42)),PanningMode=PanningMode.VerticalOnly};
            scroll.Content=_library;
            scroll.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e)
            {
                double step=Math.Max(160.0, Math.Abs(e.Delta)*1.7);
                double next=scroll.VerticalOffset-(e.Delta>0?step:-step);
                if(next<0)next=0;
                if(next>scroll.ScrollableHeight)next=scroll.ScrollableHeight;
                scroll.ScrollToVerticalOffset(next);
                e.Handled=true;
            };
            Grid.SetRow(scroll,5); left.Children.Add(scroll); Grid.SetColumn(left,0);body.Children.Add(left);

            var right=new Grid{Margin=new Thickness(18,0,0,0)}; right.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});right.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});right.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});right.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
            var selectedHeader=new Grid{Margin=new Thickness(0,0,0,6)};
            selectedHeader.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            selectedHeader.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            selectedHeader.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
            selectedHeader.Children.Add(new TextBlock{Text="Highlighted Cards",FontSize=18,FontWeight=FontWeights.Bold,Foreground=new SolidColorBrush(Color.FromRgb(214,125,255)),VerticalAlignment=VerticalAlignment.Center});
            _selectedCountText=new TextBlock{Text="0",FontSize=10,FontWeight=FontWeights.Bold,Foreground=new SolidColorBrush(Color.FromRgb(155,235,210)),Background=new SolidColorBrush(Color.FromArgb(55,155,235,210)),Padding=new Thickness(6,2,6,2),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(7,0,0,0),ToolTip="Total cards selected in this comp"};
            Grid.SetColumn(_selectedCountText,1); selectedHeader.Children.Add(_selectedCountText);
            var activeHint=new TextBlock{Text="↑ ↓ reorder  •  right-click priority",FontSize=9,Foreground=new SolidColorBrush(Color.FromRgb(162,148,178)),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(8,0,0,0)};
            Grid.SetColumn(activeHint,2); selectedHeader.Children.Add(activeHint);
            _selectedFilterRow.Orientation=Orientation.Horizontal; _selectedFilterRow.Margin=new Thickness(0,0,0,8);
            AddSelectedFilterButton("ALL",0,Color.FromRgb(195,120,255));
            AddSelectedFilterButton("CORE",1,Color.FromRgb(166,44,128));
            AddSelectedFilterButton("IMPORTANT",2,Color.FromRgb(255,137,58));
            AddSelectedFilterButton("OPTIONAL",3,Color.FromRgb(0,235,170));
            Grid.SetRow(_selectedFilterRow,1); right.Children.Add(_selectedFilterRow);
            var selScroll=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=new SolidColorBrush(Color.FromRgb(31,22,42))};selScroll.Content=_selected;Grid.SetRow(selScroll,2);right.Children.Add(selScroll);
            var bottom=new StackPanel{Orientation=Orientation.Vertical,Margin=new Thickness(0,10,0,0)};
            _status.Text="Loading current Battlegrounds pool…  •  Hotkey: CTRL + SHIFT + W"; _status.TextWrapping=TextWrapping.Wrap; _status.Foreground=new SolidColorBrush(Color.FromRgb(200,185,210));
            _poolInfoPanel=new Border{Background=new SolidColorBrush(Color.FromArgb(242,20,14,30)),BorderBrush=new SolidColorBrush(Color.FromRgb(120,62,150)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(5),Padding=new Thickness(7),Margin=new Thickness(0,0,0,6),Visibility=Visibility.Collapsed,Child=_status};
            var infoRow=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Left};
            _poolInfoButton=new Button{Content="ⓘ",Width=30,Height=26,Padding=new Thickness(0),Background=new SolidColorBrush(Color.FromArgb(205,52,27,72)),Foreground=new SolidColorBrush(Color.FromRgb(245,215,255)),BorderBrush=new SolidColorBrush(Color.FromRgb(166,44,128)),BorderThickness=new Thickness(1),ToolTip="Show pool / artwork / cache information"};
            _poolInfoButton.Click+=delegate{if(_poolInfoPanel!=null)_poolInfoPanel.Visibility=_poolInfoPanel.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;};
            infoRow.Children.Add(_poolInfoButton);
            var infoLabel=new TextBlock{Text=" Pool info",Foreground=new SolidColorBrush(Color.FromRgb(205,185,220)),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(5,0,0,0)}; infoRow.Children.Add(infoLabel);
            bottom.Children.Add(infoRow);
            bottom.Children.Add(_poolInfoPanel);
            var refresh=new Button{Content="↻ REFRESH CURRENT POOL",Padding=new Thickness(12,6,12,6),HorizontalAlignment=HorizontalAlignment.Left,Background=new SolidColorBrush(Color.FromRgb(55,38,76)),Foreground=Brushes.White,Margin=new Thickness(0,6,0,0)};
            refresh.Click+=delegate{LoadCardsAsync();}; bottom.Children.Add(refresh);
            var save=new Button{Content="SAVE COMP",Padding=new Thickness(18,8,18,8),HorizontalAlignment=HorizontalAlignment.Right,Background=new SolidColorBrush(Color.FromRgb(98,54,140)),Foreground=Brushes.White,Margin=new Thickness(0,8,0,0)};save.Click+=delegate{SaveCurrent();};bottom.Children.Add(save);Grid.SetRow(bottom,3);right.Children.Add(bottom);
            Grid.SetColumn(right,1);body.Children.Add(right);
            root.Children.Add(new Border{BorderBrush=new SolidColorBrush(Color.FromRgb(95,58,120)),BorderThickness=new Thickness(1),Background=new SolidColorBrush(Color.FromArgb(20,145,90,220)),CornerRadius=new CornerRadius(8),IsHitTestVisible=false});
            Content=root;
            BuildCompTabs();
            UpdateSelectedFilterButtons();
        }


        private void UpdateActiveButton()
        {
            if(_setActiveButton==null)return;
            bool active=_editingComp==_store.ActiveCompIndex;
            _setActiveButton.Content=active?"✓ ACTIVE IN GAME":"SET ACTIVE IN GAME";
            _setActiveButton.Background=new SolidColorBrush(active?Color.FromRgb(42,118,96):Color.FromRgb(98,54,140));
            _setActiveButton.BorderBrush=new SolidColorBrush(active?Color.FromRgb(40,235,170):Color.FromRgb(170,92,230));
            _setActiveButton.Foreground=Brushes.White;
            _setActiveButton.ToolTip=active?"This comp is currently active in-game":"Make this comp the active in-game comp";
        }

        private void BuildCompTabs()
        {
            if(_compTabsPanel==null)return;
            _compTabsPanel.Children.Clear();
            for(int i=0;i<_compTabs.Length;i++)_compTabs[i]=null;
            for(int i=0;i<_store.CompCount;i++)
            {
                int captured=i;
                var holder=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(i==0?0:5,0,0,0)};
                var b=new Button{Content=_store.GetCompName(i)+(i==_store.ActiveCompIndex?"  ✓":""),Padding=new Thickness(14,8,14,8),MinWidth=64,FontSize=13,Background=new SolidColorBrush(Color.FromRgb(55,38,76)),Foreground=Brushes.White,ToolTip="Left click: edit • Double click: rename • Right click: menu"};
                b.Click+=delegate{SwitchComp(captured);};
                b.MouseDoubleClick+=delegate{
                    if(captured>=0 && captured<_store.CompCount)
                    {
                        SaveCurrent(false);
                        _store.SetActiveComp(captured);
                        _editingComp=captured;
                        RefreshSelected();
                        UpdateTabs();
                        SetStatus(_store.GetCompName(captured)+" is now active in-game.");
                    }
                };
                var menu=new ContextMenu();
                var rename=new MenuItem{Header="Rename"}; rename.Click+=delegate{RenameComp(captured);}; menu.Items.Add(rename);
                var activate=new MenuItem{Header="Set active in game"}; activate.Click+=delegate{_store.SetActiveComp(captured);UpdateTabs();SetStatus(_store.GetCompName(captured)+" is now active in-game.");}; menu.Items.Add(activate);
                var duplicate=new MenuItem{Header="Duplicate comp"}; duplicate.Click+=delegate{ DuplicateComp(captured); }; menu.Items.Add(duplicate);
                var deleteMenu=new MenuItem{Header="Delete comp",IsEnabled=captured!=_store.ActiveCompIndex && _store.CompCount>1}; deleteMenu.Click+=delegate{ DeleteComp(captured); }; menu.Items.Add(deleteMenu);
                b.ContextMenu=menu;
                _compTabs[captured]=b;
                holder.Children.Add(b);
                if(captured!=_store.ActiveCompIndex && _store.CompCount>1)
                {
                    var trash=CreateCompactorButton();
                    trash.Click+=delegate{DeleteCompWithConfirm(captured);};
                    holder.Children.Add(trash);
                }
                _compTabsPanel.Children.Add(holder);
            }
            UpdateTabs();
            if(_addCompButton!=null){_addCompButton.IsEnabled=_store.CompCount<WishlistStore.MaxComps;_addCompButton.ToolTip=_store.CompCount<WishlistStore.MaxComps?"Add a new comp":"Maximum comps reached";}
        }

        private Button CreateCompactorButton()
        {
            var canvas = new Canvas { Width = 22, Height = 22 };
            // Futuristic "compactor": three plates narrowing toward the center with inward chevrons.
            for (int i = 0; i < 3; i++)
            {
                double w = 16 - i * 4;
                double x = (22 - w) / 2.0;
                double y = 2 + i * 6;
                var bar = new Border
                {
                    Width = w,
                    Height = 3,
                    CornerRadius = new CornerRadius(1.5),
                    Background = new SolidColorBrush(Color.FromRgb(180, 88, 255)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(255, 137, 58)),
                    BorderThickness = new Thickness(0.7)
                };
                Canvas.SetLeft(bar, x); Canvas.SetTop(bar, y); canvas.Children.Add(bar);
            }
            var left = new Polygon { Points = new PointCollection { new Point(1,18), new Point(5,16), new Point(5,20) }, Fill = new SolidColorBrush(Color.FromRgb(255, 137, 58)) };
            var right = new Polygon { Points = new PointCollection { new Point(21,18), new Point(17,16), new Point(17,20) }, Fill = new SolidColorBrush(Color.FromRgb(255, 137, 58)) };
            canvas.Children.Add(left); canvas.Children.Add(right);
            return new Button
            {
                Content = canvas,
                Width = 28,
                Height = 28,
                Margin = new Thickness(2,0,0,0),
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.FromArgb(185,34,20,48)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(166,44,128)),
                BorderThickness = new Thickness(1),
                ToolTip = "Delete inactive comp"
            };
        }

        private void DeleteCompWithConfirm(int index)
        {
            if(index==_store.ActiveCompIndex || _store.CompCount<=1) return;
            string name=_store.GetCompName(index);
            MessageBoxResult result=MessageBox.Show("Delete comp \""+name+"\"?","Delete comp",MessageBoxButton.YesNo,MessageBoxImage.Warning);
            if(result==MessageBoxResult.Yes) DeleteComp(index);
        }

        private void AddComp()
        {
            int idx=_store.AddComp("Comp "+(_store.CompCount+1));
            if(idx<0){SetStatus("Maximum number of comps reached.");return;}
            _editingComp=idx; BuildCompTabs(); RefreshSelected(); SetStatus(_store.GetCompName(idx)+" created.");
        }

        private void RenameComp(int index)
        {
            string current=_store.GetCompName(index);
            string next=PromptForText("Rename comp", "Comp name", current);
            if(string.IsNullOrWhiteSpace(next))return;
            _store.RenameComp(index,next);
            BuildCompTabs();
            SetStatus("Renamed comp to "+_store.GetCompName(index)+".");
        }

        private void DuplicateComp(int index)
        {
            string baseName=_store.GetCompName(index);
            int idx=_store.DuplicateComp(index, baseName + " Copy");
            if(idx<0){SetStatus("Could not duplicate comp.");return;}
            _editingComp=idx; BuildCompTabs(); RefreshSelected(); SetStatus(_store.GetCompName(idx)+" duplicated.");
        }

        private void DeleteComp(int index)
        {
            if(_store.CompCount<=3){SetStatus("Keep at least 3 comps.");return;}
            if(!_store.DeleteComp(index)){SetStatus("Could not delete comp.");return;}
            if(_editingComp>=_store.CompCount)_editingComp=_store.CompCount-1;
            else if(index<_editingComp)_editingComp--;
            BuildCompTabs(); RefreshSelected(); SetStatus("Comp deleted.");
        }

        private static string PromptForText(string title,string label,string initial)
        {
            var w=new Window{Title=title,Width=360,Height=150,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize,Background=new SolidColorBrush(Color.FromRgb(25,18,34))};
            var panel=new StackPanel{Margin=new Thickness(14)};
            panel.Children.Add(new TextBlock{Text=label,Foreground=Brushes.White,Margin=new Thickness(0,0,0,6)});
            var tb=new TextBox{Text=initial,Margin=new Thickness(0,0,0,10)}; panel.Children.Add(tb);
            var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
            string result=null; var ok=new Button{Content="OK",Padding=new Thickness(14,5,14,5),Margin=new Thickness(4,0,0,0)}; var cancel=new Button{Content="Cancel",Padding=new Thickness(14,5,14,5)};
            ok.Click+=delegate{result=tb.Text;w.DialogResult=true;}; cancel.Click+=delegate{w.DialogResult=false;}; buttons.Children.Add(cancel);buttons.Children.Add(ok);panel.Children.Add(buttons);w.Content=panel;w.Loaded+=delegate{tb.Focus();tb.SelectAll();}; w.ShowDialog(); return result;
        }

        private void AttachTribeDrag(Button button, string tribe)
        {
            button.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                _draggedTribeButton = button; _dragStart = e.GetPosition(_tribeRow); _draggingTribe = false;
            };
            button.PreviewMouseMove += delegate(object sender, MouseEventArgs e)
            {
                if(_draggedTribeButton!=button || e.LeftButton!=MouseButtonState.Pressed) return;
                Point p=e.GetPosition(_tribeRow);
                if(!_draggingTribe && Math.Abs(p.X-_dragStart.X)>6)
                {
                    _draggingTribe=true;
                    button.CaptureMouse();
                    button.Opacity=0.72;
                    button.RenderTransform=new TranslateTransform(0,-5);
                }
            };
            button.PreviewMouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                if(_draggedTribeButton!=button) return;
                if(_draggingTribe)
                {
                    button.ReleaseMouseCapture(); button.Opacity=1; button.RenderTransform=null;
                    e.Handled=true; ReorderTribeAt(button, e.GetPosition(_tribeRow).X);
                }
                _draggedTribeButton=null; _draggingTribe=false;
            };
        }

        private void ReorderTribeAt(Button dragged, double x)
        {
            string tribe=null; foreach(var kv in _tribeButtons) if(object.ReferenceEquals(kv.Value,dragged)){tribe=kv.Key;break;}
            if(string.IsNullOrWhiteSpace(tribe)) return;
            _tribeOrder.Remove(tribe);
            int insertIndex=_tribeOrder.Count;
            for(int i=0;i<_tribeOrder.Count;i++)
            {
                Button b=_tribeButtons[_tribeOrder[i]]; Point center=b.TranslatePoint(new Point(b.ActualWidth/2,0),_tribeRow);
                if(x<center.X){insertIndex=i;break;}
            }
            _tribeOrder.Insert(insertIndex,tribe);
            _store.SaveTribeOrder(_tribeOrder);
            RebuildTribeRow();
            SetStatus("Tribe order saved.");
        }

        private void RebuildTribeRow()
        {
            _tribeRow.Children.Clear(); _tribeButtons.Clear();
            AddTribeButton(AllTribe,"ALL",Color.FromRgb(195,120,255));
            foreach(string tribe in _tribeOrder) AddTribeButton(tribe,TribeShortLabel(tribe),TribeColor(tribe));
            UpdateFilterButtons();
        }

        private static string TribeShortLabel(string tribe)
        {
            switch((tribe??"").ToUpperInvariant())
            {
                case "ELEMENTAL": return "ELEM";
                case "QUILBOAR": return "QUIL";
                default: return tribe.ToUpperInvariant();
            }
        }

        private readonly Dictionary<string,Color> _categoryColors = new Dictionary<string,Color>(StringComparer.OrdinalIgnoreCase);
        private void AddCategoryButton(string category, string label, Color color)
        {
            Button b=new Button{Content=label,Height=30,Margin=new Thickness(_categoryRow.Children.Count==0?0:5,0,0,0),Padding=new Thickness(10,0,10,0),Background=new SolidColorBrush(Color.FromArgb(24,color.R,color.G,color.B)),Foreground=new SolidColorBrush(color),BorderBrush=new SolidColorBrush(color),BorderThickness=new Thickness(1),ToolTip="Show current BG "+category.ToLowerInvariant()+"."};
            b.Click+=delegate{_categoryFilter=category; _tribeFilter="All Tribes"; RefreshLibrary(); UpdateFilterButtons(); UpdateTribeRowVisibility(); SetStatus("Showing current BG "+category.ToLowerInvariant()+".");};
            _categoryColors[category]=color;
            _categoryButtons[category]=b; _categoryRow.Children.Add(b);
        }

        private void UpdateTribeRowVisibility()
        {
            bool show=string.Equals(_categoryFilter,"Minions",StringComparison.OrdinalIgnoreCase)
                || string.Equals(_categoryFilter,"Battlecry",StringComparison.OrdinalIgnoreCase)
                || string.Equals(_categoryFilter,"Deathrattle",StringComparison.OrdinalIgnoreCase);
            _tribeRow.Visibility=show?Visibility.Visible:Visibility.Collapsed;
        }

        private void AddTribeButton(string tribe,string label,Color color)
        {
            bool isAll=string.Equals(tribe,AllTribe,StringComparison.OrdinalIgnoreCase);
            ImageSource iconSource=isAll?null:GetTribeIcon(tribe);
            Button b=new Button{Width=isAll?50:34,Height=32,Margin=new Thickness(isAll?0:4,0,0,0),Padding=new Thickness(1),Background=new SolidColorBrush(Color.FromArgb(30,color.R,color.G,color.B)),Foreground=new SolidColorBrush(color),BorderBrush=new SolidColorBrush(color),BorderThickness=new Thickness(1),ToolTip=isAll?"All tribes":"Filter to "+tribe};
            if(iconSource!=null)
            {
                b.Content=new Image{Source=iconSource,Width=24,Height=24,Stretch=System.Windows.Media.Stretch.Uniform,SnapsToDevicePixels=true};
            }
            else
            {
                b.Content=label;
            }
            b.Click+=delegate
            {
                if(!isAll && string.Equals(_tribeFilter,tribe,StringComparison.OrdinalIgnoreCase))
                    _tribeFilter=AllTribe;
                else
                    _tribeFilter=tribe;
                RefreshLibrary();
                UpdateFilterButtons();
                SetStatus(_tribeFilter==AllTribe?"Showing all tribes in current pool.":"Showing current pool for "+_tribeFilter+".");
            };
            if(!isAll) AttachTribeDrag(b, tribe);
            _tribeButtons[tribe]=b; _tribeRow.Children.Add(b);
        }

        private void SetTierFilter(int tier)
        {
            if(tier<0) tier=0; if(tier>7) tier=7;
            _tierFilter=tier;
            RefreshLibrary();
            UpdateFilterButtons();
            SetStatus(tier==0?"Showing current BG pool, all Tavern Tiers.":"Showing current BG pool, Tavern Tier "+tier+".");
        }

        private void UpdateFilterButtons()
        {
            foreach(var kv in _categoryButtons)
            {
                bool active=string.Equals(_categoryFilter,kv.Key,StringComparison.OrdinalIgnoreCase);
                Color accent; if(!_categoryColors.TryGetValue(kv.Key,out accent)) accent=Color.FromRgb(95,210,255);
                kv.Value.Background=new SolidColorBrush(active?Color.FromArgb(80,accent.R,accent.G,accent.B):Color.FromArgb(24,accent.R,accent.G,accent.B));
                kv.Value.BorderBrush=new SolidColorBrush(active?accent:Color.FromArgb(150,accent.R,accent.G,accent.B));
                kv.Value.Foreground=new SolidColorBrush(accent); kv.Value.FontWeight=active?FontWeights.Bold:FontWeights.Normal;
            }
            for(int t=0;t<=7;t++)
            {
                Button b=_tierButtons[t]; if(b==null) continue; bool active=_tierFilter==t; Color accent=t==0?Color.FromRgb(195,120,255):TierBrushColor(t);
                b.Background=new SolidColorBrush(active?Color.FromRgb(88,55,112):Color.FromRgb(48,34,64)); b.BorderBrush=new SolidColorBrush(active?accent:Color.FromRgb(112,74,140)); b.Foreground=new SolidColorBrush(active?Colors.White:Color.FromRgb(220,205,228)); b.FontWeight=active?FontWeights.Bold:FontWeights.Normal;
            }
            foreach(var kv in _tribeButtons)
            {
                bool active=string.Equals(_tribeFilter,kv.Key,StringComparison.OrdinalIgnoreCase);
                Color accent=kv.Key==AllTribe?Color.FromRgb(195,120,255):TribeColor(kv.Key);
                kv.Value.Background=new SolidColorBrush(active?Color.FromArgb(70,accent.R,accent.G,accent.B):Color.FromArgb(25,accent.R,accent.G,accent.B));
                kv.Value.BorderBrush=new SolidColorBrush(active?accent:Color.FromArgb(140,accent.R,accent.G,accent.B)); kv.Value.Foreground=new SolidColorBrush(accent); kv.Value.FontWeight=active?FontWeights.Bold:FontWeights.Normal;
            }
        }

        private static Color TierBrushColor(int t){if(t<=3)return Color.FromRgb(55,220,190);if(t==4)return Color.FromRgb(255,205,80);if(t==5)return Color.FromRgb(255,105,105);return Color.FromRgb(245,60,150);}
        private static readonly Dictionary<string, ImageSource> BuilderTribeIconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        private static ImageSource GetTribeIcon(string tribe)
        {
            string key = (tribe ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key) || key.Equals("ALL", StringComparison.OrdinalIgnoreCase)) return null;
            string fileName = key + ".png";
            string assemblyDir = IOPath.GetDirectoryName(typeof(WishlistWindow).Assembly.Location) ?? string.Empty;
            string path = IOPath.Combine(assemblyDir, "Assets", "TribeIcons", fileName);
            ImageSource cached;
            if (BuilderTribeIconCache.TryGetValue(key, out cached)) return cached;
            if (!File.Exists(path)) return null;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.DecodePixelWidth = 64;
                bitmap.EndInit();
                bitmap.Freeze();
                BuilderTribeIconCache[key] = bitmap;
                return bitmap;
            }
            catch { return null; }
        }

        private static Color TribeColor(string tribe)
        {
            switch((tribe??"").ToUpperInvariant())
            {
                case "BEAST": return Color.FromRgb(126,220,92);
                case "DEMON": return Color.FromRgb(205,80,175);
                case "DRAGON": return Color.FromRgb(255,100,165); // rose
                case "ELEMENTAL": return Color.FromRgb(90,210,255);
                case "MECH": return Color.FromRgb(110,165,245);
                case "MURLOC": return Color.FromRgb(75,220,190);
                case "NAGA": return Color.FromRgb(85,185,240);
                case "PIRATE": return Color.FromRgb(255,205,70); // gold
                case "QUILBOAR": return Color.FromRgb(235,145,75);
                case "UNDEAD": return Color.FromRgb(175,115,230);
                default: return Color.FromRgb(195,120,255);
            }
        }

        private static string NormalizeDescriptorTribeName(string race)
        {
            if (string.IsNullOrWhiteSpace(race)) return "Neutral";
            string s = race.Trim();
            if (s.Equals("INVALID", StringComparison.OrdinalIgnoreCase) || s.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return "Neutral";
            if (s.Equals("MECHANICAL", StringComparison.OrdinalIgnoreCase) || s.Equals("MECH", StringComparison.OrdinalIgnoreCase)) return "Mech";
            if (s.Equals("QUILBOAR", StringComparison.OrdinalIgnoreCase)) return "Quilboar";
            if (s.Equals("MURLOC", StringComparison.OrdinalIgnoreCase)) return "Murloc";
            if (s.Equals("PIRATE", StringComparison.OrdinalIgnoreCase)) return "Pirate";
            if (s.Equals("DRAGON", StringComparison.OrdinalIgnoreCase)) return "Dragon";
            if (s.Equals("DEMON", StringComparison.OrdinalIgnoreCase)) return "Demon";
            if (s.Equals("BEAST", StringComparison.OrdinalIgnoreCase)) return "Beast";
            if (s.Equals("ELEMENTAL", StringComparison.OrdinalIgnoreCase)) return "Elemental";
            if (s.Equals("NAGA", StringComparison.OrdinalIgnoreCase)) return "Naga";
            if (s.Equals("UNDEAD", StringComparison.OrdinalIgnoreCase)) return "Undead";
            return s;
        }

        private static List<string> NormalizeDescriptorTribes(IEnumerable<string> tribes)
        {
            var result = new List<string>();
            if(tribes!=null)
            {
                foreach(string raw in tribes)
                {
                    string n=NormalizeDescriptorTribeName(raw);
                    if(string.IsNullOrWhiteSpace(n)) continue;
                    if(n.Equals("Invalid",StringComparison.OrdinalIgnoreCase) || n.Equals("None",StringComparison.OrdinalIgnoreCase)) n="Neutral";
                    if(!result.Contains(n,StringComparer.OrdinalIgnoreCase)) result.Add(n);
                }
            }
            if(result.Count==0) result.Add("Neutral");
            return result;
        }

        private void LoadCardsAsync()
        {
            SetStatus("Loading current Battlegrounds pool…  •  Default filter: T1");
            _library.Children.Clear();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string status;
                List<CurrentPoolCard> pool=CurrentPoolLoader.Load(out status);
                Dispatcher.BeginInvoke(new Action(delegate{ApplyCurrentPool(pool,status);}), DispatcherPriority.Background);
            });
        }

        private void ApplyCurrentPool(List<CurrentPoolCard> pool,string status)
        {
            var localPaths=BuildLocalImagePathIndex();
            var poolCards=new List<CardDescriptor>();
            foreach(CurrentPoolCard p in pool)
            {
                if(p==null||string.IsNullOrWhiteSpace(p.Id)||ShopWishlistPlugin.IsGoldenBattlegroundsVariant(p.Id))continue;
                string imagePath; ImageSource image=null;
                if(localPaths.TryGetValue(p.Id,out imagePath)) { try { image=LoadImage(imagePath); } catch { image=null; } }
                poolCards.Add(new CardDescriptor{Id=p.Id,Name=p.Name,Tribes=NormalizeDescriptorTribes(p.Tribes),Tier=p.Tier,Image=image,ImageUrl=p.ImageUrl,Category=p.Category??"Minions"});
            }
            _poolCards=poolCards.GroupBy(c=>c.Id,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).OrderBy(c=>c.Tier).ThenBy(c=>c.Name).ToList();
            _allCards=_poolCards.ToList();
            RefreshAll();
            SetStatus(status+"  •  Art: "+_allCards.Count+" / "+_poolCards.Count+" available.  •  Unarted cards hidden.  •  Golden hidden.  •  Hotkey: CTRL + SHIFT + W");
            DownloadMissingArtAsync();
        }


        private void LoadBuddiesAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string buddyStatus; List<CurrentPoolCard> buddies=CurrentPoolLoader.LoadBuddies(out buddyStatus);
                Dispatcher.BeginInvoke(new Action(delegate{ApplyBuddies(buddies,buddyStatus);}),DispatcherPriority.Background);
            });
        }

        private void ApplyBuddies(List<CurrentPoolCard> buddies,string status)
        {
            if(buddies==null||buddies.Count==0){SetStatus(_status.Text+"  •  "+status);return;}
            var localPaths=BuildLocalImagePathIndex();
            foreach(CurrentPoolCard p in buddies)
            {
                if(p==null||string.IsNullOrWhiteSpace(p.Id)||ShopWishlistPlugin.IsGoldenBattlegroundsVariant(p.Id))continue;
                string path; ImageSource image=null; if(localPaths.TryGetValue(p.Id,out path)){try{image=LoadImage(path);}catch{}}
                if(!_poolCards.Any(x=>string.Equals(x.Id,p.Id,StringComparison.OrdinalIgnoreCase)))_poolCards.Add(new CardDescriptor{Id=p.Id,Name=p.Name,Tribes=NormalizeDescriptorTribes(p.Tribes),Tier=p.Tier,Image=image,ImageUrl=p.ImageUrl,Category="Buddies"});
            }
            _poolCards=_poolCards.GroupBy(c=>c.Id,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).OrderBy(c=>c.Category).ThenBy(c=>c.Tier).ThenBy(c=>c.Name).ToList();
            _allCards=_poolCards.ToList(); RefreshAll(); SetStatus(_status.Text+"  •  "+status); DownloadMissingArtAsync();
        }

        private void DownloadMissingArtAsync()
        {
            var missing = _poolCards.Where(c=>c.Image==null && !string.IsNullOrWhiteSpace(c.ImageUrl)).ToList();
            if(missing.Count==0)return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string cacheDir=IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"HDTShopWishlist","CardArt");
                try{Directory.CreateDirectory(cacheDir);}catch{return;}

                int workerCount=Math.Min(6,Math.Max(1,missing.Count));
                int nextIndex=-1;
                int downloaded=0;
                Action worker = delegate
                {
                    while(true)
                    {
                        int i=Interlocked.Increment(ref nextIndex);
                        if(i>=missing.Count) break;
                        var c=missing[i];
                        try
                        {
                            string url=c.ImageUrl.StartsWith("http",StringComparison.OrdinalIgnoreCase)?c.ImageUrl:RemoteImageBaseUrl+c.ImageUrl;
                            string target=IOPath.Combine(cacheDir,c.Id+".png");
                            if(!File.Exists(target))
                            {
                                string fullUrl=url+(url.IndexOf("?",StringComparison.Ordinal)>=0?"&":"?")+"format=png&size=full";
                                using(var wc=new WebClient()){wc.Headers[HttpRequestHeader.UserAgent]="Mozilla/5.0 HDT-Shop-Wishlist-Overlay/0.25.46";wc.DownloadFile(fullUrl,target);}
                            }
                            ImageSource img=LoadImage(target);
                            Interlocked.Increment(ref downloaded);
                            Dispatcher.BeginInvoke(new Action(delegate
                            {
                                c.Image=img;
                                RequestLibraryRefreshThrottled();
                            }),DispatcherPriority.Background);
                        }
                        catch(Exception ex){Debug.WriteLine("Art fallback failed for "+c.Id+": "+ex.Message);}
                    }
                };
                for(int w=0;w<workerCount;w++) ThreadPool.QueueUserWorkItem(_ => worker());
                ThreadPool.QueueUserWorkItem(delegate
                {
                    while(Volatile.Read(ref nextIndex)<missing.Count) Thread.Sleep(50);
                    Dispatcher.BeginInvoke(new Action(delegate{SetStatus(_status.Text+"  •  Art ready: "+downloaded+".");}),DispatcherPriority.Background);
                });
            });
        }

        // Card-art downloads land one at a time (up to 6 in parallel, see DownloadMissingArtAsync)
        // and each one used to trigger a full RefreshLibrary(), which clears and rebuilds the
        // *entire* visible grid from scratch. With dozens/hundreds of missing images that meant
        // dozens/hundreds of full rebuilds - the actual cause of the builder feeling slow to load.
        // Coalesce those into one rebuild per ~300ms instead, while still updating progressively.
        private void RequestLibraryRefreshThrottled()
        {
            _libraryRefreshDirty = true;
            if (_libraryRefreshDebounceTimer == null)
            {
                _libraryRefreshDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(300)
                };
                _libraryRefreshDebounceTimer.Tick += delegate
                {
                    if (!_libraryRefreshDirty) return;
                    _libraryRefreshDirty = false;
                    RefreshLibrary();
                    RefreshSelected();
                };
                Closed += delegate { _libraryRefreshDebounceTimer.Stop(); };
                _libraryRefreshDebounceTimer.Start();
            }
        }

        private void RefreshAll(){BuildCompTabs();RefreshLibrary();RefreshSelected();UpdateTabs();UpdateFilterButtons();}

        public void RefreshLobbyTribes(object game)
        {
            if(game==null || !IsInGameMode) return;
            HashSet<string> detected; bool known=TryDetectLobbyAvailableTribes(game,out detected);
            if(known)
            {
                string signature=string.Join(",", detected.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase));
                string previous=string.Join(",", _lobbyAvailableTribes.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase));
                _lobbyAvailableTribes=detected;
                _lobbyTribeFilterKnown=true;
                if(!string.Equals(signature,previous,StringComparison.OrdinalIgnoreCase))
                {
                    RefreshLibrary();
                    SetStatus("Lobby tribes detected: "+string.Join(", ",_lobbyAvailableTribes.OrderBy(x=>x))+".");
                }
            }
        }

        private static string NormalizeTribeFilterName(string value)
        {
            if(string.IsNullOrWhiteSpace(value)) return string.Empty;
            string s=value.Trim();
            if(s.StartsWith("RACE_",StringComparison.OrdinalIgnoreCase)) s=s.Substring(5);
            s=s.Replace("_",string.Empty).Replace("-",string.Empty);
            if(s.Equals("MECHANICAL",StringComparison.OrdinalIgnoreCase)) return "MECH";
            if(s.Equals("QUILBOAR",StringComparison.OrdinalIgnoreCase)) return "QUILBOAR";
            return s.ToUpperInvariant();
        }

        private static bool TryDetectLobbyAvailableTribes(object game,out HashSet<string> available)
        {
            available=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var knownNames=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
                {
                    {"BACON_SUBSET_BEAST","Beast"},{"BACON_SUBSET_DEMON","Demon"},{"BACON_SUBSET_DRAGON","Dragon"},
                    {"BACON_SUBSET_ELEMENTALS","Elemental"},{"BACON_SUBSET_MECH","Mech"},{"BACON_SUBSET_MURLOC","Murloc"},
                    {"BACON_SUBSET_NAGA","Naga"},{"BACON_SUBSET_PIRATE","Pirate"},{"BACON_SUBSET_QUILLBOAR","Quilboar"},
                    {"BACON_SUBSET_UNDEAD","Undead"}
                };
                foreach(object entity in PluginReflection.EnumerateEntities(game))
                {
                    object tags=PluginReflection.GetPropertyObject(entity,"Tags");
                    if(tags==null)continue;
                    int present=0; int trueCount=0; int falseCount=0;
                    var entityValues=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
                    foreach(var pair in knownNames)
                    {
                        int value=PluginReflection.GetTagValueByNames(entity,new[]{pair.Key});
                        // We only count a tribe as part of a candidate lobby mask when the tag is actually present.
                        if(PluginReflection.HasTagKey(entity,pair.Key))
                        {
                            present++; entityValues[pair.Value]=value;
                            if(value>0)trueCount++; else falseCount++;
                        }
                    }
                    if(present>=6 && trueCount>=2 && falseCount>=1)
                    {
                        foreach(var kv in entityValues) if(kv.Value>0) available.Add(kv.Key);
                        return available.Count>=2;
                    }
                }
            }
            catch { }
            available.Clear(); return false;
        }

        private void RefreshLibrary()
        {
            // Filtering is cheap because the pool is already in memory. The expensive
            // part is creating WPF visual trees / loading & cropping art. That work is
            // now chunked across Dispatcher background turns so changing T1/T2/... does
            // not freeze the whole builder for a noticeable amount of time.
            int generation = ++_libraryRefreshGeneration;
            IEnumerable<CardDescriptor> cards=_allCards==null?Enumerable.Empty<CardDescriptor>():_allCards.Where(c=>c!=null && (!string.IsNullOrWhiteSpace(c.ImageUrl) || c.Image!=null));
            // Battlecry/Deathrattle are keyword filters, not a stored Category: they match any card
            // (whatever its own Category) whose rules text mentions that keyword, so a Deathrattle
            // Tavern Spell or Buddy would surface here too, not just Minions.
            bool isKeywordCategory = string.Equals(_categoryFilter,"Battlecry",StringComparison.OrdinalIgnoreCase) || string.Equals(_categoryFilter,"Deathrattle",StringComparison.OrdinalIgnoreCase);
            if (isKeywordCategory)
                cards=cards.Where(c=>CardTextContainsKeyword(c.Id,_categoryFilter));
            else
                cards=cards.Where(c=>string.Equals(c.Category,_categoryFilter,StringComparison.OrdinalIgnoreCase));
            if((string.Equals(_categoryFilter,"Minions",StringComparison.OrdinalIgnoreCase) || isKeywordCategory) && _lobbyTribeFilterKnown)
                cards=cards.Where(c=>c.Tribes.Any(t=>string.Equals(t,"Neutral",StringComparison.OrdinalIgnoreCase) || _lobbyAvailableTribes.Contains(NormalizeTribeFilterName(t))));
            if(!string.Equals(_categoryFilter,"Tavern Spells",StringComparison.OrdinalIgnoreCase) && !string.Equals(_categoryFilter,"Buddies",StringComparison.OrdinalIgnoreCase) && !string.Equals(_tribeFilter,AllTribe,StringComparison.OrdinalIgnoreCase))
                cards=cards.Where(c=>c.HasTribe(_tribeFilter));
            if(_tierFilter>0) cards=cards.Where(c=>c.Tier==_tierFilter);
            if(!string.IsNullOrWhiteSpace(_searchQuery))
                cards=cards.Where(c=>CardMatchesSearch(c,_searchQuery));

            var ordered=cards.OrderBy(x=>x.Tier).ThenBy(x=>x.Name).ToList();
            var selectedIds=new HashSet<string>(_store.GetCompIds(_editingComp),StringComparer.OrdinalIgnoreCase);
            _library.Children.Clear();
            RenderLibraryBatch(ordered,selectedIds,generation,0);
        }

        private static readonly Dictionary<string,string> CardTextCache = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        private static string GetCardText(string cardId)
        {
            if(string.IsNullOrWhiteSpace(cardId)) return string.Empty;
            string cached;
            if(CardTextCache.TryGetValue(cardId,out cached)) return cached;
            string text=string.Empty;
            try
            {
                HearthDb.Card card;
                if(HearthDb.Cards.All!=null && HearthDb.Cards.All.TryGetValue(cardId,out card) && card!=null && !string.IsNullOrWhiteSpace(card.Text))
                    text=card.Text;
            }
            catch { }
            CardTextCache[cardId]=text;
            return text;
        }

        private static bool CardTextContainsKeyword(string cardId,string keyword)
        {
            string text=GetCardText(cardId);
            return !string.IsNullOrWhiteSpace(text) && text.IndexOf(keyword,StringComparison.OrdinalIgnoreCase)>=0;
        }

        // "Smart" search: matches the card name, its rules text, or any known BG keyword
        // (Battlecry, Deathrattle, Taunt, Divine Shield, etc.) mentioned in that text.
        private static readonly string[] SearchableKeywords = { "Battlecry","Deathrattle","Taunt","Divine Shield","Reborn","Windfury","Poisonous","Magnetic","Avenge","Stealth","Lifesteal","Rush","Charm","Frenzy","Overkill","Combo","Adapt","Discover","Tradeable" };
        private static bool CardMatchesSearch(CardDescriptor card,string query)
        {
            if(card==null) return false;
            if(!string.IsNullOrWhiteSpace(card.Name) && card.Name.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0) return true;
            string text=GetCardText(card.Id);
            if(!string.IsNullOrWhiteSpace(text) && text.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0) return true;
            foreach(string keyword in SearchableKeywords)
                if(keyword.IndexOf(query,StringComparison.OrdinalIgnoreCase)>=0 && !string.IsNullOrWhiteSpace(text) && text.IndexOf(keyword,StringComparison.OrdinalIgnoreCase)>=0)
                    return true;
            return false;
        }

        private void RenderLibraryBatch(List<CardDescriptor> cards, HashSet<string> selectedIds, int generation, int startIndex)
        {
            if(generation!=_libraryRefreshGeneration) return;
            const int batchSize=12;
            int end=Math.Min(cards.Count,startIndex+batchSize);
            for(int i=startIndex;i<end;i++)
            {
                CardDescriptor c=cards[i];
                var b=new Button
                {
                    Width=104, Height=108, Margin=new Thickness(2), Padding=new Thickness(0),
                    Background=Brushes.Transparent, Foreground=Brushes.White, BorderBrush=Brushes.Transparent,
                    BorderThickness=new Thickness(0), Cursor=Cursors.Hand, IsHitTestVisible=true,
                    ToolTip=c.Name+"\\n"+c.Category+"\\n"+c.TribeLabel+"\\nTier "+(c.Tier>0?c.Tier.ToString():"?")
                };

                var artBorder=new Border
                {
                    Width=96, Height=96, Background=new SolidColorBrush(Color.FromRgb(16,12,22)),
                    BorderBrush=selectedIds.Contains(c.Id)?new SolidColorBrush(Color.FromRgb(0,235,210)):new SolidColorBrush(Color.FromArgb(130,110,76,145)),
                    BorderThickness=new Thickness(selectedIds.Contains(c.Id)?2:1), CornerRadius=new CornerRadius(7), ClipToBounds=true,
                    HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center, SnapsToDevicePixels=true
                };

                var art=new Image
                {
                    Source=GetBuilderArtOnlyImage(c), Width=96, Height=96, Stretch=Stretch.UniformToFill,
                    HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center, SnapsToDevicePixels=true
                };
                RenderOptions.SetBitmapScalingMode(art, BitmapScalingMode.HighQuality);

                var selectedBadge=new Border
                {
                    Width=24, Height=24, CornerRadius=new CornerRadius(12), Background=new SolidColorBrush(Color.FromRgb(8,38,36)),
                    BorderBrush=new SolidColorBrush(Color.FromRgb(0,235,210)), BorderThickness=new Thickness(2),
                    HorizontalAlignment=HorizontalAlignment.Right, VerticalAlignment=VerticalAlignment.Top, Margin=new Thickness(0,5,5,0),
                    Visibility=selectedIds.Contains(c.Id)?Visibility.Visible:Visibility.Collapsed, IsHitTestVisible=false,
                    Child=new TextBlock{Text="✓",Foreground=new SolidColorBrush(Color.FromRgb(165,255,240)),FontSize=15,FontWeight=FontWeights.Bold,
                        HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,TextAlignment=TextAlignment.Center}
                };
                var artCanvas=new Grid(); artCanvas.Children.Add(art); artCanvas.Children.Add(selectedBadge); artBorder.Child=artCanvas; b.Content=artBorder;

                b.Click+=delegate{AddCard(c,1); RefreshLibrary();};
                b.MouseEnter+=delegate
                {
                    artBorder.BorderBrush=new SolidColorBrush(Color.FromRgb(0,235,210));
                    artBorder.BorderThickness=new Thickness(selectedIds.Contains(c.Id)?2:1);
                    artBorder.Effect=new DropShadowEffect{Color=Color.FromRgb(0,235,210),BlurRadius=18,ShadowDepth=0,Opacity=0.58};
                    art.RenderTransformOrigin=new Point(0.5,0.5); art.RenderTransform=new ScaleTransform(1.045,1.045);
                };
                b.MouseLeave+=delegate
                {
                    artBorder.BorderBrush=selectedIds.Contains(c.Id)?new SolidColorBrush(Color.FromRgb(0,235,210)):new SolidColorBrush(Color.FromArgb(130,110,76,145));
                    artBorder.BorderThickness=new Thickness(selectedIds.Contains(c.Id)?2:1); artBorder.Effect=null; art.RenderTransform=null;
                };
                _library.Children.Add(b);
            }

            if(end<cards.Count)
                Dispatcher.BeginInvoke(new Action(delegate{RenderLibraryBatch(cards,selectedIds,generation,end);}),DispatcherPriority.Background);
        }

        private static readonly Dictionary<string, ImageSource> BuilderArtSourceCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> BuilderArtDownloadStarted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private ImageSource GetBuilderArtOnlyImage(CardDescriptor card)
        {
            if(card==null) return null;
            string id=card.Id??string.Empty;
            if(id.Length==0) return card.Image;
            try
            {
                ImageSource cached;
                if(BuilderArtSourceCache.TryGetValue(id,out cached)) return cached;

                string cacheDir=IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"HDTShopWishlist","CardArtOnly");
                string webp=IOPath.Combine(cacheDir,id+".webp");
                string jpg=IOPath.Combine(cacheDir,id+".jpg");
                string png=IOPath.Combine(cacheDir,id+".png");
                string path=File.Exists(webp)?webp:(File.Exists(jpg)?jpg:(File.Exists(png)?png:null));
                if(path!=null)
                {
                    cached=TrimWhiteMargins(LoadImage(path), id);
                    BuilderArtSourceCache[id]=cached;
                    return cached;
                }

                StartBuilderArtDownload(card);
            }
            catch { }
            return TrimWhiteMargins(GetCardArtOnlyImage(card.Image, id), id);
        }

        private void StartBuilderArtDownload(CardDescriptor card)
        {
            if(card==null || string.IsNullOrWhiteSpace(card.Id)) return;
            string id=card.Id.Trim();
            lock(BuilderArtDownloadStarted)
            {
                if(BuilderArtDownloadStarted.Contains(id)) return;
                BuilderArtDownloadStarted.Add(id);
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string cacheDir=IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"HDTShopWishlist","CardArtOnly");
                    Directory.CreateDirectory(cacheDir);
                    string target=IOPath.Combine(cacheDir,id+".webp");
                    if(!File.Exists(target))
                    {
                        string url=RemoteArtOnlyBaseUrl+Uri.EscapeDataString(id)+".webp";
                        using(var wc=new WebClient())
                        {
                            wc.Headers[HttpRequestHeader.UserAgent]="Mozilla/5.0 HDT-Shop-Wishlist-Overlay/0.25.37";
                            wc.DownloadFile(url,target);
                        }
                    }
                    ImageSource img=TrimWhiteMargins(LoadImage(target), id);
                    lock(BuilderArtSourceCache) BuilderArtSourceCache[id]=img;
                    Dispatcher.BeginInvoke(new Action(delegate{RequestLibraryRefreshThrottled();}),DispatcherPriority.Background);
                }
                catch(Exception ex)
                {
                    Debug.WriteLine("Builder art-only download failed for "+id+": "+ex.Message);
                }
                finally
                {
                    lock(BuilderArtDownloadStarted) BuilderArtDownloadStarted.Remove(id);
                }
            });
        }

        private static readonly Dictionary<string, ImageSource> CardArtOnlyCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, ImageSource> TrimmedArtCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        private static ImageSource TrimWhiteMargins(ImageSource source, string cacheKey)
        {
            if (source == null) return null;
            try
            {
                string key = cacheKey ?? string.Empty;
                ImageSource cached;
                if (key.Length > 0 && TrimmedArtCache.TryGetValue(key, out cached)) return cached;
                BitmapSource bitmap = source as BitmapSource;
                if (bitmap == null) return source;
                var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                int w = converted.PixelWidth, h = converted.PixelHeight;
                if (w < 20 || h < 20) return source;
                int stride = w * 4;
                byte[] pixels = new byte[stride * h];
                converted.CopyPixels(pixels, stride, 0);
                const int white = 238;
                bool IsWhitePixel(int x, int y)
                {
                    int i = y * stride + x * 4;
                    byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
                    return a < 8 || (r >= white && g >= white && b >= white);
                }
                bool RowHasArt(int y)
                {
                    int step = Math.Max(1, w / 32), nonWhite = 0, samples = 0;
                    for (int x = 0; x < w; x += step) { samples++; if (!IsWhitePixel(x, y)) nonWhite++; }
                    return nonWhite >= Math.Max(2, (int)Math.Ceiling(samples * 0.35));
                }
                bool ColHasArt(int x)
                {
                    int step = Math.Max(1, h / 32), nonWhite = 0, samples = 0;
                    for (int y = 0; y < h; y += step) { samples++; if (!IsWhitePixel(x, y)) nonWhite++; }
                    return nonWhite >= Math.Max(2, (int)Math.Ceiling(samples * 0.35));
                }
                int top = 0, bottom = h - 1, left = 0, right = w - 1;
                int maxTrimX = Math.Max(1, (int)(w * 0.22));
                int maxTrimY = Math.Max(1, (int)(h * 0.10));
                while (top < bottom && top < maxTrimY && !RowHasArt(top)) top++;
                while (bottom > top && (h - 1 - bottom) < maxTrimY && !RowHasArt(bottom)) bottom--;
                while (left < right && left < maxTrimX && !ColHasArt(left)) left++;
                while (right > left && (w - 1 - right) < maxTrimX && !ColHasArt(right)) right--;
                // Only apply the trim when there is a meaningful outer gutter. Keep a small breathing room.
                if (left == 0 && right == w - 1 && top == 0 && bottom == h - 1) return source;
                int pad = 1;
                left = Math.Max(0, left - pad); right = Math.Min(w - 1, right + pad);
                top = Math.Max(0, top - pad); bottom = Math.Min(h - 1, bottom + pad);
                int cw = right - left + 1, ch = bottom - top + 1;
                if (cw < w * 0.75 && ch < h * 0.75) return source;
                var crop = new CroppedBitmap(converted, new Int32Rect(left, top, cw, ch));
                crop.Freeze();
                if (key.Length > 0) TrimmedArtCache[key] = crop;
                return crop;
            }
            catch { return source; }
        }

        private static ImageSource GetCardArtOnlyImage(ImageSource source, string cardId)
        {
            if (source == null) return null;
            string key = cardId ?? string.Empty;
            try
            {
                ImageSource cached;
                if (key.Length > 0 && CardArtOnlyCache.TryGetValue(key, out cached)) return cached;

                BitmapSource bitmap = source as BitmapSource;
                if (bitmap == null) return source;
                if (bitmap.PixelWidth < 20 || bitmap.PixelHeight < 20) return source;

                // Full-card art supplied by hsbg.cards follows the standard Hearthstone frame.
                // Keep the central illustration window only: this removes the white source gutters,
                // mana/card-frame edges, nameplate and effect box while preserving the illustration itself.
                int x = (int)Math.Round(bitmap.PixelWidth * 0.135);
                int y = (int)Math.Round(bitmap.PixelHeight * 0.075);
                int w = (int)Math.Round(bitmap.PixelWidth * 0.730);
                int h = (int)Math.Round(bitmap.PixelHeight * 0.555);
                if (x < 0) x = 0;
                if (y < 0) y = 0;
                if (x + w > bitmap.PixelWidth) w = bitmap.PixelWidth - x;
                if (y + h > bitmap.PixelHeight) h = bitmap.PixelHeight - y;
                if (w < 10 || h < 10) return source;

                var crop=new CroppedBitmap(bitmap,new Int32Rect(x,y,w,h));
                crop.Freeze();
                ImageSource result=crop;
                if (key.Length > 0) CardArtOnlyCache[key]=result;
                return result;
            }
            catch { return source; }
        }

        private void AddSelectedFilterButton(string label, int priority, Color color)
        {
            Button b=new Button{Content=label,Padding=new Thickness(7,3,7,3),Margin=new Thickness(_selectedFilterRow.Children.Count==0?0:4,0,0,0),Background=new SolidColorBrush(Color.FromArgb(36,color.R,color.G,color.B)),Foreground=new SolidColorBrush(color),BorderBrush=new SolidColorBrush(color),BorderThickness=new Thickness(1),FontWeight=FontWeights.Bold,FontSize=8,MinWidth=42,ToolTip=priority==0?"Show all selected cards":"Show selected "+label.ToLowerInvariant()+" cards"};
            b.Click+=delegate{_selectedPriorityFilter=priority; UpdateSelectedFilterButtons(); RefreshSelected();};
            b.Tag=priority;
            _selectedFilterRow.Children.Add(b);
        }

        private void UpdateSelectedFilterButtons()
        {
            foreach(object child in _selectedFilterRow.Children)
            {
                Button b=child as Button; if(b==null) continue;
                int p=0; if(b.Tag is int) p=(int)b.Tag;
                Color color=p==0?Color.FromRgb(195,120,255):p==1?Color.FromRgb(166,44,128):p==2?Color.FromRgb(255,137,58):Color.FromRgb(0,235,170);
                bool active=p==_selectedPriorityFilter;
                b.Background=new SolidColorBrush(Color.FromArgb(active?(byte)235:(byte)36,color.R,color.G,color.B));
                b.Foreground=active?Brushes.White:new SolidColorBrush(color);
                b.BorderBrush=new SolidColorBrush(color);
                b.BorderThickness=new Thickness(active?2:1);
            }
        }

        private void RefreshSelected()
        {
            _selected.Opacity = 0;
            if(_selectedCountText!=null) _selectedCountText.Text=_store.GetCompIds(_editingComp).Count().ToString();
            _selected.Children.Clear();
            var localPaths = BuildLocalImagePathIndex();
            foreach(string id in _store.GetCompIds(_editingComp))
            {
                CardDescriptor card=_allCards.FirstOrDefault(x=>string.Equals(x.Id,id,StringComparison.OrdinalIgnoreCase));
                if (card == null)
                {
                    try
                    {
                        HearthDb.Card localCard;
                        if (Cards.All != null && Cards.All.TryGetValue(id, out localCard) && localCard != null)
                        {
                            card = new CardDescriptor { Id = id, Name = string.IsNullOrWhiteSpace(localCard.Name) ? id : localCard.Name, Tier = GetLocalCardTier(localCard), Tribes = GetLocalCardTribes(localCard), Category = GetLocalCategory(localCard), Image = null };
                        }
                    }
                    catch { }
                }

                ImageSource selectedImage = card != null ? card.Image : null;
                if (selectedImage == null)
                {
                    try
                    {
                        string imagePath;
                        if(localPaths.TryGetValue(id,out imagePath))
                            selectedImage=LoadImage(imagePath);
                    }
                    catch { selectedImage=null; }
                }
                if (card != null && card.Image == null && selectedImage != null)
                    card.Image = selectedImage;

                int priority=_store.GetPriority(_editingComp,id);
                if(_selectedPriorityFilter>0 && priority!=_selectedPriorityFilter) continue;
                Border row=new Border{Margin=new Thickness(0,0,0,6),Background=PriorityRowBrush(priority),CornerRadius=new CornerRadius(3),AllowDrop=false,Cursor=Cursors.Arrow};
                Grid rowGrid=new Grid();
                row.Child=rowGrid;
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});

                string displayName = card != null ? card.Name : ResolveCardName(id);
                var nameThumb=new Grid{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6,2,2,2)};
                nameThumb.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star),MinWidth=0});
                nameThumb.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
                TextBlock name=new TextBlock{Text=displayName,Foreground=PriorityTextBrush(priority),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Stretch,FontWeight=FontWeights.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis,MinWidth=0,Margin=new Thickness(0,0,5,0)};
                Grid.SetColumn(name,0); nameThumb.Children.Add(name);

                Border thumbFrame=new Border
                {
                    Width=40,
                    Height=48,
                    Margin=new Thickness(0,1,4,1),
                    Padding=new Thickness(1),
                    CornerRadius=new CornerRadius(4),
                    Background=new SolidColorBrush(Color.FromArgb(115,20,14,32)),
                    BorderBrush=PriorityBorderBrush(priority),
                    BorderThickness=new Thickness(1),
                    VerticalAlignment=VerticalAlignment.Center,
                    HorizontalAlignment=HorizontalAlignment.Right,
                    ToolTip=displayName
                };
                if(selectedImage!=null)
                {
                    thumbFrame.Child=new Image{Source=selectedImage,Width=36,Height=44,Stretch=Stretch.Uniform};
                }
                else
                {
                    thumbFrame.Child=new TextBlock{Text="?",Foreground=PriorityTextBrush(priority),FontWeight=FontWeights.Bold,FontSize=12,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
                }
                Grid.SetColumn(thumbFrame,1); nameThumb.Children.Add(thumbFrame);
                Grid.SetColumn(nameThumb,0); rowGrid.Children.Add(nameThumb);

                var priorityPanel=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(2,0,2,0)};
                Button priorityButton=new Button
                {
                    Width=72,
                    Height=26,
                    Padding=new Thickness(3,0,3,0),
                    Content=PriorityLabel(priority),
                    Foreground=PriorityTextBrush(priority),
                    Background=PriorityFillBrush(priority),
                    BorderBrush=PriorityBorderBrush(priority),
                    BorderThickness=new Thickness(1),
                    FontWeight=FontWeights.SemiBold,
                    FontSize=8,
                    IsHitTestVisible=true,
                    Focusable=true
                };
                // Deliberately bypass Button.Click/ContextMenu: these were the source of
                // the regression. Directly handle mouse-up on the control and write the
                // priority immediately, matching the proven v0.15 interaction model.
                priorityButton.PreviewMouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
                {
                    CyclePriority(id);
                    e.Handled=true;
                };
                priorityButton.PreviewMouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e)
                {
                    CyclePriority(id);
                    e.Handled=true;
                };
                priorityButton.ToolTip = "Click to cycle priority";
                if(selectedImage!=null) priorityButton.ToolTip=BuildPriorityVisualToolTip(selectedImage, priority);
                priorityPanel.Children.Add(priorityButton);
                Grid.SetColumn(priorityPanel,2); rowGrid.Children.Add(priorityPanel);

                var orderPanel=new Grid{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(1,1,1,1)};
                orderPanel.RowDefinitions.Add(new RowDefinition{Height=new GridLength(17)});
                orderPanel.RowDefinitions.Add(new RowDefinition{Height=new GridLength(17)});
                Button upButton=new Button{Content="▲",Width=21,Height=17,Padding=new Thickness(0),Margin=new Thickness(0,0,0,1),Background=new SolidColorBrush(Color.FromArgb(70,38,25,60)),Foreground=new SolidColorBrush(Color.FromRgb(235,205,255)),BorderBrush=new SolidColorBrush(Color.FromRgb(150,95,220)),BorderThickness=new Thickness(1),FontSize=8,ToolTip="Move card up"};
                Button downButton=new Button{Content="▼",Width=21,Height=17,Padding=new Thickness(0),Margin=new Thickness(0,1,0,0),Background=new SolidColorBrush(Color.FromArgb(70,38,25,60)),Foreground=new SolidColorBrush(Color.FromRgb(235,205,255)),BorderBrush=new SolidColorBrush(Color.FromRgb(150,95,220)),BorderThickness=new Thickness(1),FontSize=8,ToolTip="Move card down"};
                upButton.Click+=delegate{ _store.MoveCardRelative(_editingComp,id,-1); RefreshSelected(); SetStatus(displayName+" moved up."); };
                downButton.Click+=delegate{ _store.MoveCardRelative(_editingComp,id,1); RefreshSelected(); SetStatus(displayName+" moved down."); };
                Grid.SetRow(upButton,0); orderPanel.Children.Add(upButton);
                Grid.SetRow(downButton,1); orderPanel.Children.Add(downButton);
                Grid.SetColumn(orderPanel,3); rowGrid.Children.Add(orderPanel);

                Button remove=new Button{Content="⨯",Width=24,Height=24,Margin=new Thickness(2),Padding=new Thickness(0),Background=new SolidColorBrush(Color.FromArgb(60,25,18,45)),Foreground=Brushes.White,BorderBrush=new SolidColorBrush(Color.FromRgb(175,110,235)),BorderThickness=new Thickness(1),FontSize=11,FontWeight=FontWeights.Bold,ToolTip="Remove from comp"};
                remove.Click+=delegate{RemoveCard(id);}; Grid.SetColumn(remove,5); rowGrid.Children.Add(remove);

                if(selectedImage!=null)
                {
                    var tipPanel=new Border{Background=new SolidColorBrush(Color.FromRgb(27,18,36)),BorderBrush=new SolidColorBrush(Color.FromRgb(112,74,140)),BorderThickness=new Thickness(1),Padding=new Thickness(6)};
                    var tipStack=new StackPanel();
                    tipStack.Children.Add(new Image{Source=selectedImage,Width=150,Height=188,Stretch=Stretch.Uniform});
                    tipStack.Children.Add(new TextBlock{Text=displayName,Foreground=Brushes.White,FontWeight=FontWeights.Bold,TextAlignment=TextAlignment.Center,Margin=new Thickness(4,4,4,2)});
                    tipStack.Children.Add(new TextBlock{Text=string.Equals(card != null ? card.Category : "", "Buddies",StringComparison.OrdinalIgnoreCase)?"BUDDY":"T"+(card != null && card.Tier>0?card.Tier.ToString():"?")+" • "+(card != null ? card.TribeLabel : ""),Foreground=new SolidColorBrush(TribeColor(card != null ? card.Tribes.FirstOrDefault() : null)),TextAlignment=TextAlignment.Center,Margin=new Thickness(4,0,4,6)});
                    tipPanel.Child=tipStack; row.ToolTip=tipPanel; ToolTipService.SetInitialShowDelay(row,80); ToolTipService.SetBetweenShowDelay(row,40); ToolTipService.SetShowDuration(row,10000);
                }
                // Selected-card reordering is handled by explicit up/down buttons.
                row.MouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e)
                {
                    CyclePriority(id);
                    e.Handled = true;
                };
                _selected.Children.Add(row);
            }
            _selected.Opacity = 1.0;
        }

        private static T FindAncestor<T>(DependencyObject source) where T : DependencyObject
        {
            DependencyObject current = source;
            while (current != null)
            {
                T typed = current as T;
                if (typed != null) return typed;
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private static int GetLocalCardTier(HearthDb.Card card)
        {
            try { return card == null ? 0 : card.Entity.GetTag(GameTag.TECH_LEVEL); } catch { return 0; }
        }

        private static List<string> GetLocalCardTribes(HearthDb.Card card)
        {
            try
            {
                var descriptor = new List<string>();
                foreach (string tribe in new[] { "Beast","Demon","Dragon","Elemental","Mech","Murloc","Naga","Pirate","Quilboar","Undead" })
                {
                    string[] names = tribe == "Elemental" ? new[] { "BACON_SUBSET_ELEMENTAL", "BACON_SUBSET_ELEMENTALS" } :
                                     tribe == "Mech" ? new[] { "BACON_SUBSET_MECH", "BACON_SUBSET_MECHS" } :
                                     new[] { "BACON_SUBSET_" + tribe.ToUpperInvariant(), "BACON_SUBSET_" + tribe.ToUpperInvariant() + "S" };
                    foreach (string n in names)
                    {
                        GameTag tag;
                        if (Enum.TryParse<GameTag>(n, true, out tag) && card.Entity.GetTag(tag) > 0) { descriptor.Add(tribe); break; }
                    }
                }
                return descriptor.Count == 0 ? new List<string> { "Neutral" } : descriptor;
            }
            catch { return new List<string> { "Neutral" }; }
        }

        private static string GetLocalCategory(HearthDb.Card card)
        {
            try
            {
                int type = card.Entity.GetTag(GameTag.CARDTYPE);
                int school = card.Entity.GetTag(GameTag.SPELL_SCHOOL);
                if (type == 42 && school == 9) return "Tavern Spells";
                if (card.Entity.GetTag(GameTag.BACON_BUDDY) == 1) return "Buddies";
            }
            catch { }
            return "Minions";
        }

        private static string ResolveCardName(string id)
        {
            try
            {
                HearthDb.Card card;
                if (Cards.All != null && Cards.All.TryGetValue(id, out card) && card != null && !string.IsNullOrWhiteSpace(card.Name)) return card.Name;
            }
            catch { }
            return id;
        }

        private static Brush CreateSplitBorderBrush(Color first, Color second)
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            g.GradientStops.Add(new GradientStop(first, 0));
            g.GradientStops.Add(new GradientStop(first, 0.42));
            g.GradientStops.Add(new GradientStop(second, 0.58));
            g.GradientStops.Add(new GradientStop(second, 1));
            return g;
        }

        private static ToolTip BuildPriorityVisualToolTip(ImageSource image, int currentPriority)
        {
            int next=currentPriority==1?2:currentPriority==2?3:1;
            Border left=new Border{Width=150,Height=190,Background=new SolidColorBrush(Color.FromRgb(25,18,35)),BorderBrush=PriorityBorderBrush(currentPriority),BorderThickness=new Thickness(3),CornerRadius=new CornerRadius(6),Padding=new Thickness(4)};
            left.Child=new Image{Source=image,Stretch=Stretch.Uniform};
            TextBlock arrow=new TextBlock{Text="➜",Foreground=new SolidColorBrush(Color.FromRgb(220,220,235)),FontSize=30,FontWeight=FontWeights.Bold,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(8,0,8,0)};
            Border right=new Border{Width=150,Height=190,Background=new SolidColorBrush(Color.FromRgb(25,18,35)),BorderBrush=PriorityBorderBrush(next),BorderThickness=new Thickness(3),CornerRadius=new CornerRadius(6),Padding=new Thickness(4)};
            right.Child=new Image{Source=image,Stretch=Stretch.Uniform};
            StackPanel panel=new StackPanel{Orientation=Orientation.Horizontal,Background=new SolidColorBrush(Color.FromRgb(16,11,24))};
            panel.Children.Add(left); panel.Children.Add(arrow); panel.Children.Add(right);
            return new ToolTip{Content=panel,Placement=System.Windows.Controls.Primitives.PlacementMode.Mouse};
        }

        private static string PriorityLabel(int p){return p==1?"★ CORE":p==2?"◆ IMPORTANT":"• OPTIONAL";}
        // Priority palette is intentionally aligned with the in-game highlight language:
        // CORE = deep-violet/gold, IMPORTANT = bright gold, OPTIONAL = electric emerald/teal.
        private static Brush PriorityTextBrush(int p){if(p==1)return new SolidColorBrush(Color.FromRgb(255,220,250));if(p==2)return new SolidColorBrush(Color.FromRgb(255,224,190));return new SolidColorBrush(Color.FromRgb(180,255,235));}
        private static Brush PriorityFillBrush(int p){if(p==1)return new SolidColorBrush(Color.FromArgb(255,116,38,108));if(p==2)return new SolidColorBrush(Color.FromArgb(255,134,62,18));return new SolidColorBrush(Color.FromArgb(255,0,110,92));}
        private static Brush PriorityBorderBrush(int p){if(p==1)return CreateSplitBorderBrush(Color.FromRgb(166,44,128),Color.FromRgb(255,205,65));if(p==2)return new SolidColorBrush(Color.FromRgb(255,137,58));return new SolidColorBrush(Color.FromRgb(0,235,170));}
        private static Brush PriorityMenuBrush(int p){if(p==1)return new SolidColorBrush(Color.FromRgb(88,30,72));if(p==2)return new SolidColorBrush(Color.FromRgb(112,48,16));return new SolidColorBrush(Color.FromRgb(0,92,78));}
        private static Brush PriorityRowBrush(int p){if(p==1)return new SolidColorBrush(Color.FromArgb(238,60,22,58));if(p==2)return new SolidColorBrush(Color.FromArgb(238,70,28,12));return new SolidColorBrush(Color.FromArgb(238,16,62,52));}

        private static Brush TierBrush(int t){if(t<=0)return new SolidColorBrush(Color.FromRgb(145,80,235));if(t<=3)return new SolidColorBrush(Color.FromRgb(35,220,205));if(t==4)return new SolidColorBrush(Color.FromRgb(255,205,75));if(t==5)return new SolidColorBrush(Color.FromRgb(255,105,65));return new SolidColorBrush(Color.FromRgb(235,60,150));}
        private static string PrioritySymbol(int p){return PriorityLabel(p);}
        private void AddCard(CardDescriptor c,int priority){if(c==null||ShopWishlistPlugin.IsGoldenBattlegroundsVariant(c.Id))return;var current=_store.GetPriority(_editingComp,c.Id);if(current>0){SetStatus(c.Name+" is already selected. Right-click it to change priority.");return;}var list=_store.GetCompIds(_editingComp).Select(id=>Tuple.Create(id,_store.GetPriority(_editingComp,id))).ToList();list.Add(Tuple.Create(c.Id,priority));_store.SaveComp(_editingComp,list);RefreshSelected();SetStatus(c.Name+" added as Core. Right-click to change priority.");}
        private void RemoveCard(string id){var list=_store.GetCompIds(_editingComp).Where(x=>!string.Equals(x,id,StringComparison.OrdinalIgnoreCase)).Select(x=>Tuple.Create(x,_store.GetPriority(_editingComp,x)));_store.SaveComp(_editingComp,list);RefreshSelected();}
        private void CyclePriority(string id)
        {
            int current=_store.GetPriority(_editingComp,id);
            int next=current<=0?1:(current==3?1:current+1);
            SetPriority(id,next);
        }
        private void SetPriority(string id,int p)
        {
            if(string.IsNullOrWhiteSpace(id))return;
            p=WishlistStore.ClampPriority(p);
            // Change exactly one card in-place so any number of cards can share a priority.
            _store.SetCardPriority(_editingComp,id,p);
            RefreshSelected();
            SetStatus(id+" → "+PrioritySymbol(p));
        }
        private void SwitchComp(int i){if(i<0||i>=_store.CompCount)return;SaveCurrent(false);_editingComp=i;RefreshSelected();UpdateTabs();if(_windowTitleText!=null)_windowTitleText.Text=(IsInGameMode?"BG COMP BUILDER  •  ":"Battlegrounds Comp Builder")+_store.GetCompName(_editingComp);if(_inGameActiveBadge!=null)_inGameActiveBadge.Visibility=IsInGameMode?Visibility.Visible:Visibility.Collapsed;}
        private void SaveCurrent(bool close=false){_store.SaveComp(_editingComp,_store.GetCompIds(_editingComp).Select(id=>Tuple.Create(id,_store.GetPriority(_editingComp,id))));if(_onSaved!=null)_onSaved();UpdateTabs();if(close)Close();}
        private void UpdateTabs()
        {
            for(int i=0;i<_store.CompCount;i++)
            {
                Button b=_compTabs[i]; if(b==null)continue;
                bool editing=i==_editingComp; bool active=i==_store.ActiveCompIndex;
                b.Background=new SolidColorBrush(editing?Color.FromRgb(98,54,140):Color.FromRgb(55,38,76));
                b.BorderBrush=new SolidColorBrush(active?Color.FromRgb(40,235,170):Color.FromRgb(112,74,140));
                b.BorderThickness=new Thickness(active?2:1);
                b.Foreground=new SolidColorBrush(active?Color.FromRgb(185,255,232):Colors.White);
                b.FontWeight=editing?FontWeights.Bold:FontWeights.Normal;
                b.Content=_store.GetCompName(i)+(active?"  ✓":"");
                b.ToolTip=active?"Active in-game comp":"Click to edit • Double-click or menu to set active";
            }
            if(_addCompButton!=null)
            {
                _addCompButton.IsEnabled=_store.CompCount<WishlistStore.MaxComps;
                _addCompButton.ToolTip=_store.CompCount<WishlistStore.MaxComps?"Add a new comp":"Maximum comps reached";
            }
            UpdateActiveButton();
        }
        private void SetStatus(string s){_status.Text=s;}



        private static Dictionary<string,string> BuildLocalImagePathIndex()
        {
            var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            string appData=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir=IOPath.Combine(appData,"HearthstoneDeckTracker","Images","CardImages");
            if(!Directory.Exists(dir)){string legacy=IOPath.Combine(appData,"Hearthstone Deck Tracker","Images","CardImages");if(Directory.Exists(legacy))dir=legacy;}
            if(!Directory.Exists(dir))return result;
            try
            {
                foreach(string path in Directory.EnumerateFiles(dir))
                {
                    string ext=IOPath.GetExtension(path); if(!string.Equals(ext,".png",StringComparison.OrdinalIgnoreCase)&&!string.Equals(ext,".jpg",StringComparison.OrdinalIgnoreCase)&&!string.Equals(ext,".jpeg",StringComparison.OrdinalIgnoreCase))continue;
                    string stem=IOPath.GetFileNameWithoutExtension(path); if(ShopWishlistPlugin.IsGoldenBattlegroundsVariant(stem))continue;
                    if(!stem.StartsWith("BG",StringComparison.OrdinalIgnoreCase)&&!stem.StartsWith("BGS_",StringComparison.OrdinalIgnoreCase))continue;
                    if(result.ContainsKey(stem))continue; result[stem]=path;
                }
            }catch{}
            return result;
        }
        private static ImageSource LoadImage(string path){var b=new BitmapImage();b.BeginInit();b.CacheOption=BitmapCacheOption.OnLoad;b.UriSource=new Uri(path,UriKind.Absolute);b.EndInit();b.Freeze();return b;}
    }

    internal static class PluginReflection
    {
        public static object GetProperty(object source,string propertyName){if(source==null)return null;try{PropertyInfo p=source.GetType().GetProperty(propertyName,BindingFlags.Instance|BindingFlags.Public);return p==null?null:p.GetValue(source,null);}catch{return null;}}
        public static bool GetBool(object source,string propertyName){object v=GetProperty(source,propertyName);return v is bool&&(bool)v;}
        public static int GetInt(object source,string propertyName){object v=GetProperty(source,propertyName);if(v==null)return 0;try{return Convert.ToInt32(v);}catch{return 0;}}
        public static string GetString(object source,string propertyName){object v=GetProperty(source,propertyName);return v==null?null:v.ToString();}
        public static bool HasTagKey(object entity,string tagName)
        {
            try
            {
                object tags=GetProperty(entity,"Tags"); if(tags==null)return false;
                IEnumerable keys=GetProperty(tags,"Keys") as IEnumerable; if(keys==null)return false;
                foreach(object key in keys) if(string.Equals(Convert.ToString(key),tagName,StringComparison.OrdinalIgnoreCase)) return true;
            } catch { }
            return false;
        }
        public static object GetPropertyObject(object obj,string propertyName){return GetProperty(obj,propertyName);}
        public static object TryInvoke(object source,string methodName,object[] args)
        {
            if(source==null || string.IsNullOrWhiteSpace(methodName)) return null;
            try
            {
                MethodInfo[] methods=source.GetType().GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                foreach(MethodInfo m in methods)
                {
                    if(!string.Equals(m.Name,methodName,StringComparison.OrdinalIgnoreCase)) continue;
                    ParameterInfo[] p=m.GetParameters();
                    object[] a=args??new object[0];
                    if(p.Length!=a.Length) continue;
                    return m.Invoke(source,a);
                }
            }catch{}
            return null;
        }
        public static int GetZoneTagValue(object entity){return GetTagValueByNames(entity,new[]{"ZONE"});}
        public static bool HasTag(object entity,string tagName){if(entity==null||string.IsNullOrWhiteSpace(tagName))return false;object tags=GetProperty(entity,"Tags");if(tags==null)return false;IEnumerable keys=GetProperty(tags,"Keys") as IEnumerable;if(keys==null)return false;foreach(object key in keys){if(string.Equals(Convert.ToString(key),tagName,StringComparison.OrdinalIgnoreCase))return true;}return false;}
        public static int GetTagValueByNames(object entity,IEnumerable<string> tagNames){object tags=GetProperty(entity,"Tags");if(tags==null)return 0;IEnumerable keys=GetProperty(tags,"Keys") as IEnumerable;if(keys==null)return 0;foreach(object key in keys){string keyName=Convert.ToString(key)??string.Empty;foreach(string desired in tagNames){if(!string.Equals(keyName,desired,StringComparison.OrdinalIgnoreCase))continue;try{PropertyInfo item=tags.GetType().GetProperty("Item");if(item==null)return 0;object value=item.GetValue(tags,new object[]{key});return Convert.ToInt32(value);}catch{return 0;}}}return 0;}
        public static IEnumerable<object> EnumerateEntities(object game){object ents=GetProperty(game,"Entities");object vals=GetProperty(ents,"Values");IEnumerable en=vals as IEnumerable;if(en==null)yield break;foreach(object e in en)yield return e;}
        public static IEnumerable<object> EnumerateBoardEntitiesFromState(object state)
        {
            if(state==null) yield break;
            foreach(string name in new[]{"Board","Minions","FriendlyBoard","OpposingBoard","Friendly","Opposing","BoardState"})
            {
                object value=GetProperty(state,name); IEnumerable seq=value as IEnumerable; if(seq==null || value is string) continue;
                foreach(object item in seq){if(item==null)continue;string id=GetString(item,"CardId");if(!string.IsNullOrWhiteSpace(id))yield return item;}
            }
        }
    }

    internal sealed class BattlegroundsScryMemory
    {
        private static int FirstPositive(params int[] values)
        {
            foreach (int v in values)
                if (v > 0) return v;
            return 0;
        }

        private static string NormalizeLobbyTribe(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Neutral";
            string s = value.Trim();
            if (s.StartsWith("RACE_", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(5);
            s = s.Replace("_", string.Empty).Replace("-", string.Empty);
            if (s.Equals("MECHANICAL", StringComparison.OrdinalIgnoreCase)) return "Mech";
            if (s.Equals("ELEMENTALS", StringComparison.OrdinalIgnoreCase)) return "Elemental";
            if (s.Equals("QUILLBOAR", StringComparison.OrdinalIgnoreCase)) return "Quilboar";
            if (s.Equals("NONE", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("INVALID", StringComparison.OrdinalIgnoreCase)) return "Neutral";
            var singulars = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Beast", "Beast" }, { "Beasts", "Beast" },
                { "Demon", "Demon" }, { "Demons", "Demon" },
                { "Dragon", "Dragon" }, { "Dragons", "Dragon" },
                { "Elemental", "Elemental" }, { "Elementals", "Elemental" },
                { "Mech", "Mech" }, { "Mechs", "Mech" },
                { "Murloc", "Murloc" }, { "Murlocs", "Murloc" },
                { "Naga", "Naga" }, { "Nagas", "Naga" },
                { "Pirate", "Pirate" }, { "Pirates", "Pirate" },
                { "Quilboar", "Quilboar" }, { "Quilboars", "Quilboar" },
                { "Undead", "Undead" }, { "Undeads", "Undead" }
            };
            string canonical;
            if (singulars.TryGetValue(s, out canonical)) return canonical;
            return s;
        }


        public sealed class RailTile
        {
            public int Order;
            public int Team;
            public int EntityId;
            public int PlayerId;
            public int DuoTeammatePlayerId;
            public bool DuoFightsFirstKnown;
            public bool DuoFightsFirst;
            public int HeroEntityTier;
            public string HeroCardId;
            public string NativeTribe;
            public int NativeCount;
            public int NativeTier;
            public object Handle;
        }

        private static readonly Lazy<BattlegroundsScryMemory> _lazy = new Lazy<BattlegroundsScryMemory>(() => new BattlegroundsScryMemory());
        public static BattlegroundsScryMemory Instance { get { return _lazy.Value; } }
        private MonoImage _image;
        private string _detectedUnity;
        private DateTime _lastAttempt = DateTime.MinValue;
        public bool IsAvailable { get { return Image != null; } }

        private MonoImage Image
        {
            get
            {
                if (_image != null) return _image;
                if ((DateTime.UtcNow - _lastAttempt).TotalSeconds < 3) return null;
                _lastAttempt = DateTime.UtcNow;
                try
                {
                    Process[] procs = Process.GetProcessesByName("Hearthstone");
                    Process proc = procs.FirstOrDefault();
                    if (proc == null) return null;
                    string unity = DetectUnityVersion(proc);
                    using (MonoScry scry = new MonoScry(Scry.connect(proc.Id)))
                    {
                        if (!string.IsNullOrWhiteSpace(unity))
                        {
                            _image = scry.getImage(new List<string> { "Blizzard.T5.ServiceLocator" }, unity);
                            if (_image != null) return _image;
                        }
                        if (!string.Equals(unity, "2022.3.62.7762112", StringComparison.Ordinal))
                            _image = scry.getImage(new List<string> { "Blizzard.T5.ServiceLocator" }, "2022.3.62.7762112");
                    }
                    return _image;
                }
                catch { return null; }
            }
        }

        public void ForceRebind()
        {
            try
            {
                _image = null;
                _lastAttempt = DateTime.MinValue;
            }
            catch { }
        }

        // Manual troubleshooting hotkey (Ctrl+Shift+R): drop the current memory binding and force a
        // hard wait before the next reconnect attempt is even allowed, by re-using the existing 3s
        // Image-getter throttle instead of reconnecting immediately like ForceRebind() does.
        public void Disconnect()
        {
            try
            {
                _image = null;
                _lastAttempt = DateTime.UtcNow;
            }
            catch { }
        }

        public dynamic GetLeaderboardManager()
        {
            try { return Image?["PlayerLeaderboardManager"]?["s_instance"]; } catch { return null; }
        }

        // One-off investigation aid (Ctrl+Shift+M): dump every loaded Mono class whose full name
        // contains any of the given needles, plus - for a few likely singleton-holder candidates -
        // their instance field names/values, to a log file. Used to locate the real Tavern shop
        // card manager instead of guessing screen coordinates.
        public void DumpMatchingClassesToFile(string logPath, params string[] needles)
        {
            try
            {
                var img = Image;
                using (var w = new StreamWriter(logPath, false))
                {
                    w.WriteLine("scan at " + DateTime.Now);
                    if (img == null) { w.WriteLine("Image is null (no Hearthstone/Mono binding)."); return; }
                    MonoClass[] classes;
                    try { classes = img.getClasses(); } catch (Exception ex) { w.WriteLine("getClasses() failed: " + ex); return; }
                    w.WriteLine("total classes: " + (classes == null ? 0 : classes.Length));
                    if (classes == null) return;
                    var matches = new List<string>();
                    foreach (var c in classes)
                    {
                        string full = null;
                        try { full = c.getFullName(); } catch { }
                        if (string.IsNullOrEmpty(full)) continue;
                        foreach (string needle in needles)
                        {
                            if (full.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) { matches.Add(full); break; }
                        }
                    }
                    w.WriteLine("matches: " + matches.Count);
                    foreach (string m in matches.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)) w.WriteLine("  " + m);
                }
            }
            catch (Exception ex) { try { File.WriteAllText(logPath, "DumpMatchingClassesToFile failed: " + ex); } catch { } }
        }

        // Dump a Mono class's FIELD DESCRIPTORS (names + declared types), no instance needed.
        // Use this first to find the right static instance-holder field name before drilling in.
        public void DumpClassFieldNamesToFile(string logPath, string className)
        {
            try
            {
                var img = Image;
                using (var w = new StreamWriter(logPath, false))
                {
                    w.WriteLine("scan at " + DateTime.Now + " class=" + className);
                    if (img == null) { w.WriteLine("Image is null."); return; }
                    dynamic cls = img[className];
                    if (cls == null) { w.WriteLine("class not found: " + className); return; }
                    Dictionary<string, MonoClassField> fields;
                    try { fields = (Dictionary<string, MonoClassField>)PluginReflection.TryInvoke(cls, "getFields", new object[0]); }
                    catch (Exception ex) { w.WriteLine("getFields failed: " + ex); return; }
                    if (fields == null) { w.WriteLine("getFields returned null"); return; }
                    foreach (var kv in fields.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        string typeName = "?"; object staticVal = null; bool hasStatic = false;
                        try { dynamic t = kv.Value.getType(); typeName = (string)PluginReflection.TryInvoke(t, "ToString", new object[0]) ?? t.ToString(); } catch { }
                        try { staticVal = kv.Value.getStaticValue(); hasStatic = true; } catch { }
                        w.WriteLine("  " + kv.Key + "  : " + typeName + (hasStatic ? ("   static=" + (staticVal == null ? "null" : staticVal.ToString())) : ""));
                    }
                }
            }
            catch (Exception ex) { try { File.WriteAllText(logPath, "DumpClassFieldNamesToFile failed: " + ex); } catch { } }
        }

        // Dump the instance field names/values of the live singleton (s_instance-style) for a
        // given class full name, plus one level of drill-down into any field whose value looks
        // like another Mono object (so we can walk toward card slot data without guessing blind).
        public void DumpInstanceFieldsToFile(string logPath, string className, params string[] instanceFieldCandidates)
        {
            try
            {
                var img = Image;
                using (var w = new StreamWriter(logPath, false))
                {
                    w.WriteLine("scan at " + DateTime.Now + " class=" + className);
                    if (img == null) { w.WriteLine("Image is null."); return; }
                    dynamic cls = img[className];
                    if (cls == null) { w.WriteLine("class not found: " + className); return; }
                    dynamic instance = null;
                    foreach (string f in instanceFieldCandidates)
                    {
                        try { instance = cls[f]; if (instance != null) { w.WriteLine("instance via field: " + f); break; } } catch { }
                    }
                    if (instance == null) { w.WriteLine("no instance found via: " + string.Join(",", instanceFieldCandidates)); return; }
                    Dictionary<string, object> fields;
                    try { fields = (Dictionary<string, object>)PluginReflection.TryInvoke(instance, "getFields", new object[0]); }
                    catch (Exception ex) { w.WriteLine("getFields failed: " + ex); return; }
                    if (fields == null) { w.WriteLine("getFields returned null"); return; }
                    foreach (var kv in fields.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        string valStr;
                        try { valStr = kv.Value == null ? "null" : (kv.Value.ToString() + "  [" + kv.Value.GetType().FullName + "]"); }
                        catch (Exception ex) { valStr = "<error: " + ex.Message + ">"; }
                        w.WriteLine("  " + kv.Key + " = " + valStr);
                    }
                }
            }
            catch (Exception ex) { try { File.WriteAllText(logPath, "DumpInstanceFieldsToFile failed: " + ex); } catch { } }
        }

        public int GetDynamicCount(dynamic obj)
        {
            if (obj == null) return 0;
            try { return (int)obj.size(); } catch { }
            try { return Convert.ToInt32(obj.Count); } catch { }
            try { return Convert.ToInt32(obj.Length); } catch { }
            return 0;
        }

        public static int ReadEntityId(object card)
        {
            return ReadIntPath(card as dynamic,
                new [] { "m_overlay", "m_heroActor", "m_entity", "m_entityId" },
                new [] { "m_overlay", "m_heroActor", "m_entity", "m_id" },
                new [] { "m_overlay", "m_heroActor", "m_entity", "EntityId" },
                new [] { "m_overlay", "m_heroActor", "m_entity", "Id" });
        }

        public static int ReadPlayerId(object card)
        {
            return ReadIntPath(card as dynamic,
                new [] { "m_overlay", "m_heroActor", "m_entity", "m_playerId" },
                new [] { "m_overlay", "m_heroActor", "m_entity", "m_controller" },
                new [] { "m_overlay", "m_heroActor", "m_entity", "PlayerId" });
        }





        public bool TryFindLeaderboardTeamForIdentity(int playerId, int entityId, out int teamIndex, out int slotIndex, out string source)
        {
            teamIndex = -1; slotIndex = -1; source = null;
            try
            {
                dynamic mgr = Image?["PlayerLeaderboardManager"]?["s_instance"];
                if (mgr == null) return false;
                dynamic teams = mgr?["m_teams"]?["_items"];
                if (teams == null) return false;
                int teamCount = GetDynamicCount(teams);
                for (int t = 0; t < teamCount; t++)
                {
                    dynamic team = teams[(uint)t];
                    dynamic cards = team?["m_playerLeaderboardCards"]?["_items"];
                    if (cards == null) continue;
                    int cardCount = GetDynamicCount(cards);
                    for (int c = 0; c < cardCount; c++)
                    {
                        object card = cards[(uint)c];
                        if (card == null) continue;
                        int eid = ReadEntityId(card);
                        int pid = ReadPlayerId(card);
                        object hero = ReadObjectPath(card as dynamic, "m_overlay", "m_heroActor", "m_entity");
                        if (hero != null)
                        {
                            eid = FirstPositive(eid, PluginReflection.GetInt(hero, "Id"), PluginReflection.GetInt(hero, "EntityId"));
                            pid = FirstPositive(pid, PluginReflection.GetTagValueByNames(hero, new[] { "PLAYER_ID", "BACON_PLAYER_ID", "CONTROLLER" }));
                        }
                        if ((entityId > 0 && eid == entityId) || (playerId > 0 && pid == playerId))
                        {
                            teamIndex = t; slotIndex = c; source = hero != null ? "leaderboard-card-hero-entity" : "leaderboard-card";
                            return true;
                        }
                    }
                }
            } catch { }
            return false;
        }

        public RailTile ReadLeaderboardTileForTeam(int linearSeatIndex)
        {
            if (linearSeatIndex < 0) return null;
            try
            {
                dynamic mgr = Image?["PlayerLeaderboardManager"]?["s_instance"];
                if (mgr == null) return null;
                dynamic teams = mgr["m_teams"]?["_items"];
                if (teams == null) return null;

                int linear = 0;
                for (uint t = 0; t < teams.size(); t++)
                {
                    dynamic team = teams[t];
                    dynamic cards = team?["m_playerLeaderboardCards"]?["_items"];
                    if (cards == null) continue;
                    for (uint c = 0; c < cards.size(); c++)
                    {
                        if (linear != linearSeatIndex)
                        {
                            linear++;
                            continue;
                        }

                        dynamic card = cards[c];
                        if (card == null) return null;
                        return ReadRailTileFromLeaderboardCard(card, (int)t, linearSeatIndex);
                    }
                }
            }
            catch { return null; }
            return null;
        }

        public List<RailTile> ReadLeaderboardTiles()
        {
            var result = new List<RailTile>();
            try
            {
                dynamic mgr = Image?["PlayerLeaderboardManager"]?["s_instance"];
                if (mgr == null) return result;
                dynamic teams = mgr["m_teams"]?["_items"];
                if (teams == null) return result;
                int order = 0;
                for (uint t = 0; t < teams.size(); t++)
                {
                    dynamic team = teams[t];
                    dynamic cards = team?["m_playerLeaderboardCards"]?["_items"];
                    if (cards == null) continue;
                    for (uint c = 0; c < cards.size(); c++)
                    {
                        dynamic card = cards[c];
                        if (card == null) continue;
                        RailTile tile = ReadRailTileFromLeaderboardCard(card, (int)t, order++);
                        if (tile != null) result.Add(tile);
                    }
                }
            }
            catch { }
            return result;
        }

        private RailTile ReadRailTileFromLeaderboardCard(dynamic card, int teamIndex, int order)
        {
            try
            {
                string hero = ReadStringPath(card,
                    new [] { "m_overlay", "m_heroActor", "m_entity", "m_cardIdInternal" },
                    new [] { "m_overlay", "m_heroActor", "m_entity", "m_cardId" },
                    new [] { "m_heroCardId" });
                int entityId = ReadIntPath(card,
                    new [] { "m_overlay", "m_heroActor", "m_entity", "m_entityId" },
                    new [] { "m_overlay", "m_heroActor", "m_entity", "m_id" },
                    new [] { "m_overlay", "m_heroActor", "m_entity", "EntityId" },
                    new [] { "m_overlay", "m_heroActor", "m_entity", "Id" });
                int playerId = ReadIntPath(card as dynamic,
                    new [] { "m_overlay", "m_heroActor", "m_entity", "m_playerId" },
                    new [] { "m_overlay", "m_heroActor", "m_entity", "m_controller" });
                object heroEntity = ReadObjectPath(card as dynamic, "m_overlay", "m_heroActor", "m_entity");
                int heroTier = PluginReflection.GetTagValueByNames(heroEntity, new[] { "PLAYER_TECH_LEVEL", "TECH_LEVEL", "BG_TECH_LEVEL", "BACON_TECH_LEVEL", "TAVERN_TIER" });
                int heroPlayerId = PluginReflection.GetTagValueByNames(heroEntity, new[] { "PLAYER_ID", "BACON_PLAYER_ID", "CONTROLLER" });
                int duoMate = PluginReflection.GetTagValueByNames(heroEntity, new[] { "BACON_DUO_TEAMMATE_PLAYER_ID", "DUO_TEAMMATE_PLAYER_ID" });
                bool hasFightsFirstTag = PluginReflection.HasTag(heroEntity, "BACON_DUO_PLAYER_FIGHTS_FIRST_NEXT_COMBAT") || PluginReflection.HasTag(heroEntity, "DUO_PLAYER_FIGHTS_FIRST_NEXT_COMBAT");
                int fightsFirstRaw = PluginReflection.GetTagValueByNames(heroEntity, new[] { "BACON_DUO_PLAYER_FIGHTS_FIRST_NEXT_COMBAT", "DUO_PLAYER_FIGHTS_FIRST_NEXT_COMBAT" });
                playerId = FirstPositive(playerId, heroPlayerId);
                string nativeTribe; int nativeCount; int nativeTier;
                TryReadNativeRecentCombatsSummary(card, out nativeTribe, out nativeCount, out nativeTier);
                return new RailTile
                {
                    Order = order, Team = teamIndex, EntityId = entityId, PlayerId = playerId,
                    DuoTeammatePlayerId = duoMate,
                    DuoFightsFirstKnown = hasFightsFirstTag,
                    DuoFightsFirst = fightsFirstRaw > 0,
                    HeroEntityTier = heroTier, HeroCardId = hero, NativeTribe = nativeTribe,
                    NativeCount = nativeCount, NativeTier = nativeTier, Handle = card
                };
            }
            catch { return null; }
        }

        private static bool TryReadNativeRecentCombatsSummary(dynamic card, out string tribe, out int count, out int tier)
        {
            tribe = null; count = 0; tier = 0;
            string panelTribe = null; int panelCount = 0; int panelTier = 0;
            try
            {
                object panel = ReadObjectPath(card, "m_overlay", "m_recentCombatsPanel");
                if (panel != null)
                {
                    object nameObject = ReadObjectPath(panel, "m_singleTribeWithCountName");
                    object numberObject = ReadObjectPath(panel, "m_singleTribeWithCountNumber");
                    string nativeName = ReadMonoDisplayText(nameObject, 0);
                    string nativeNumber = ReadMonoDisplayText(numberObject, 0);
                    int parsedCount = ParseFirstInteger(nativeNumber);
                    if (!string.IsNullOrWhiteSpace(nativeName) && nativeName.IndexOf("mixed", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        panelTribe = "Mixed"; panelCount = 0;
                        panelTier = FirstPositive(ReadIntPath(panel, new [] { "m_techLevelCount" }));
                    }
                    string normalized = NormalizeLobbyTribe(nativeName);
                    if (parsedCount >= 0 && parsedCount <= 7 && IsKnownLobbyTribe(normalized))
                    {
                        panelTribe = normalized; panelCount = parsedCount;
                        panelTier = FirstPositive(ReadIntPath(panel, new [] { "m_techLevelCount" }));
                    }
                }
            }
            catch { }

            string mapTribe; int mapCount;
            if (TryReadNativeRaceCounts(card, out mapTribe, out mapCount))
            {
                tribe = mapTribe; count = mapCount; tier = panelTier; return true;
            }
            if (!string.IsNullOrWhiteSpace(panelTribe))
            {
                tribe = panelTribe; count = panelCount; tier = panelTier; return true;
            }
            return false;
        }

        private static bool TryReadNativeRaceCounts(dynamic card, out string tribe, out int count)
        {
            tribe = null; count = 0;
            try
            {
                object overlay = ReadObjectPath(card, "m_overlay");
                object raceCounts = ReadObjectPath(overlay, "m_raceCounts");
                if (raceCounts == null) return false;
                dynamic keys = ReadObjectPath(raceCounts, "keySlots");
                dynamic values = ReadObjectPath(raceCounts, "valueSlots");
                if (keys == null || values == null) return false;
                int keyLen = -1, valLen = -1;
                try { keyLen = (int)keys.size(); } catch { try { keyLen = Convert.ToInt32(keys.Length); } catch { } }
                try { valLen = (int)values.size(); } catch { try { valLen = Convert.ToInt32(values.Length); } catch { } }
                if (keyLen < 0 || valLen < 0) return false;
                int best = -1; string bestName = null; int tiesAtBest = 0; int positiveKnownTribes = 0;
                int cap = Math.Min(Math.Min(keyLen, valLen), 32);
                for (int i = 0; i < cap; i++)
                {
                    object kr = null, vr = null;
                    try { kr = keys[(uint)i]; } catch { try { kr = keys[i]; } catch { } }
                    try { vr = values[(uint)i]; } catch { try { vr = values[i]; } catch { } }
                    int key = 0; try { key = Convert.ToInt32(kr); } catch { try { key = Convert.ToInt32(ReadObjectPath(kr, "value__")); } catch { } }
                    int val = 0; try { val = Convert.ToInt32(vr); } catch { try { val = Convert.ToInt32(ReadObjectPath(vr, "value__")); } catch { } }
                    if (val < 0 || val > 7) continue;
                    string n = null; try { n = Enum.GetName(typeof(HearthDb.Enums.Race), key); } catch { }
                    n = NormalizeLobbyTribe(n);
                    if (!IsKnownLobbyTribe(n)) continue;
                    if (val > 0) positiveKnownTribes++;
                    if (val > best) { best = val; bestName = n; tiesAtBest = 1; }
                    else if (val == best && val > 0) { tiesAtBest++; }
                }
                // Report the dominant (highest-count) tribe whenever there is a clear single
                // majority, exactly like the native hover tooltip does. Only fall back to "Mixed"
                // when two or more tribes are genuinely tied for the top count - a board simply
                // having a secondary/off-tribe minion alongside a dominant tribe is not "mixed".
                if (best > 0 && tiesAtBest == 1 && !string.IsNullOrWhiteSpace(bestName)) { tribe = bestName; count = best; return true; }
                if (positiveKnownTribes > 1) { tribe = "Mixed"; count = 0; return true; }
                if (best >= 0 && !string.IsNullOrWhiteSpace(bestName)) { tribe = bestName; count = best; return true; }
            }
            catch { }
            return false;
        }

private static bool IsKnownLobbyTribe(string tribe)
        {
            string s = NormalizeLobbyTribe(tribe);
            return new [] { "Beast", "Demon", "Dragon", "Elemental", "Mech", "Murloc", "Naga", "Pirate", "Quilboar", "Undead" }
                .Any(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase));
        }

        private static string ReadMonoDisplayText(object value, int depth)
        {
            if (value == null || depth > 3) return null;
            if (value is string) return ((string)value).Trim();
            try
            {
                string direct = Convert.ToString(value);
                if (!string.IsNullOrWhiteSpace(direct) && direct.IndexOf("ScryDotNet", StringComparison.OrdinalIgnoreCase) < 0 && direct.IndexOf("MonoObject", StringComparison.OrdinalIgnoreCase) < 0 && direct.IndexOf("UberText", StringComparison.OrdinalIgnoreCase) < 0)
                    return direct.Trim();
            }
            catch { }

            foreach (string key in new [] { "m_text", "text", "Text", "m_Text", "m_value", "value", "m_cachedText", "m_localizedText", "m_label" })
            {
                object nested = ReadObjectPath(value, key);
                if (nested == null || ReferenceEquals(nested, value)) continue;
                string text = nested is string ? Convert.ToString(nested) : ReadMonoDisplayText(nested, depth + 1);
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }

            try
            {
                dynamic mono = value;
                dynamic clazz = mono.getClass();
                dynamic fields = clazz.getFields();
                var dict = fields as System.Collections.IDictionary;
                if (dict != null)
                {
                    foreach (System.Collections.DictionaryEntry entry in dict)
                    {
                        string key = Convert.ToString(entry.Key) ?? string.Empty;
                        object fieldValue = entry.Value;
                        if (key.IndexOf("text", StringComparison.OrdinalIgnoreCase) < 0 && key.IndexOf("value", StringComparison.OrdinalIgnoreCase) < 0 && key.IndexOf("label", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (fieldValue is string str && !string.IsNullOrWhiteSpace(str)) return str.Trim();
                        string nestedText = ReadMonoDisplayText(fieldValue, depth + 1);
                        if (!string.IsNullOrWhiteSpace(nestedText)) return nestedText.Trim();
                    }
                }
            }
            catch { }
            return null;
        }

        private static int ParseFirstInteger(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var m = System.Text.RegularExpressions.Regex.Match(text, @"\b([0-9]{1,2})\b");
            int n;
            return m.Success && int.TryParse(m.Groups[1].Value, out n) ? n : 0;
        }

        private static object ReadObjectPath(dynamic root, params string[] path)
        {
            try
            {
                dynamic cur = root;
                foreach (string part in path) cur = cur?[part];
                return cur;
            }
            catch { return null; }
        }

        private static string ReadStringPath(dynamic root, params string[][] paths)
        {
            foreach (string[] path in paths)
            {
                try
                {
                    dynamic cur = root;
                    foreach (string part in path) cur = cur?[part];
                    string s = Convert.ToString(cur);
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
                catch { }
            }
            return null;
        }

        private static int ReadIntPath(dynamic root, params string[][] paths)
        {
            foreach (string[] path in paths)
            {
                try
                {
                    dynamic cur = root;
                    foreach (string part in path) cur = cur?[part];
                    if (cur == null) continue;
                    int n;
                    if (int.TryParse(Convert.ToString(cur), out n) && n > 0) return n;
                }
                catch { }
            }
            return 0;
        }

        private string DetectUnityVersion(Process proc)
        {
            if (!string.IsNullOrWhiteSpace(_detectedUnity)) return _detectedUnity;
            try
            {
                string exeDir = IOPath.GetDirectoryName(proc.MainModule.FileName);
                foreach (string dir in new [] { exeDir, IOPath.GetDirectoryName(exeDir ?? string.Empty) })
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string dll = IOPath.Combine(dir, "UnityPlayer.dll");
                    if (!File.Exists(dll)) continue;
                    string v = FileVersionInfo.GetVersionInfo(dll).FileVersion;
                    if (!string.IsNullOrWhiteSpace(v)) { _detectedUnity = v; return v; }
                }
            }
            catch { }
            return null;
        }
    }



    internal static class Native
    {
        public const int GWL_EXSTYLE=-20;public const int WS_EX_LAYERED=0x00080000;public const int WS_EX_TRANSPARENT=0x00000020;public const int WS_EX_TOOLWINDOW=0x00000080;public const int WS_EX_NOACTIVATE=0x08000000;public const int WM_HOTKEY=0x0312;public const uint MOD_CONTROL=0x0002;public const uint MOD_SHIFT=0x0004;
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd,out RECT rect);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        public const uint SWP_NOZORDER=0x0004; public const uint SWP_NOACTIVATE=0x0010;
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd,out uint processId);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd,int nIndex);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd,int nIndex,int dwNewLong);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd,int id,uint fsModifiers,uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd,int id);

        // Shared, throttled Hearthstone process/window lookup. Every overlay window (shop
        // highlight, launcher icon, tavern rail, comp builder) used to run its own independent
        // Process.GetProcessesByName("Hearthstone") + GetWindowRect scan, and the foreground
        // check did the same again - up to 5 separate process-table scans per ~100ms tick. All
        // of that now shares one cached scan, re-run at most once every 60ms.
        private static DateTime _hsScanAt = DateTime.MinValue;
        private static bool _hsFound;
        private static IntPtr _hsHandle = IntPtr.Zero;
        private static RECT _hsRect;
        private static bool _hsForeground;

        private static void RefreshHearthstoneScan()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _hsScanAt).TotalMilliseconds < 60) return;
            _hsScanAt = now;
            _hsFound = false;
            _hsForeground = false;
            try
            {
                IntPtr fg = GetForegroundWindow();
                uint fgPid = 0;
                if (fg != IntPtr.Zero) GetWindowThreadProcessId(fg, out fgPid);
                foreach (Process p in Process.GetProcessesByName("Hearthstone"))
                {
                    try
                    {
                        if (p.MainWindowHandle == IntPtr.Zero) continue;
                        RECT r;
                        if (GetWindowRect(p.MainWindowHandle, out r))
                        {
                            _hsHandle = p.MainWindowHandle;
                            _hsRect = r;
                            _hsFound = true;
                            if (fgPid != 0 && (uint)p.Id == fgPid) _hsForeground = true;
                            break;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static bool TryFindHearthstoneWindow(out RECT rect, out IntPtr handle)
        {
            RefreshHearthstoneScan();
            rect = _hsRect; handle = _hsHandle;
            return _hsFound;
        }

        public static bool IsForegroundHearthstone() { RefreshHearthstoneScan(); return _hsForeground; }
        public struct RECT{public int Left;public int Top;public int Right;public int Bottom;}
        public struct POINT{public int X;public int Y;}
    }
}
