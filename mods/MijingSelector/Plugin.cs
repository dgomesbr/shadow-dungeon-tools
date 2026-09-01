using System;
using System.Globalization;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using FinkFramework.Runtime.Singleton;
using Mijing;
using UI.Panels;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MijingSelector;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "custom.mijingselector";
    public const string PluginName = "Mijing Floor Selector";
    public const string PluginVersion = "1.0.2";

    // Window id from the reserved 49300-49399 range: 49313 chosen for MijingSelector.
    private const int WindowId = 49313;

    private const int MaxTargetFloor = 99999;

    // Precondition reasons (const so the per-frame check never allocates).
    private const string ReasonNoSave = "No save loaded.";
    private const string ReasonNotUnlocked = "Mijing is not unlocked on this save yet.";
    private const string ReasonEntering = "Already entering a Mijing floor - wait for the load.";
    private const string ReasonWrongScene = "Must stand in the home town (HomeScene) or inside a Mijing floor.";
    private const string ReasonNoIds = "Mijing level list not loaded yet (enter the home town once).";

    internal static ManualLogSource Log;
    internal static ConfigEntry<bool> AllowRaisingCap;

    // The live plugin instance. ModMenuProvider must be static (the menu finds it by reflection)
    // but the window state lives on the MonoBehaviour. Null before Awake / after OnDestroy, so
    // every menu delegate null-checks it.
    internal static Plugin Instance;

    private bool _show;
    private bool _broken; // set once if the Mijing API fails at runtime; window disabled from then on
    // See OnGUI: the window must not make its first appearance on an input event.
    private bool _layoutArmed;
    private Rect _rect = new Rect(40f, 40f, 340f, 100f);

    private int _targetFloor = 1;
    private string _targetFloorText = "1";
    private bool _confirmCapRaise;
    private string _status = "";

    // Cached label strings, rebuilt only when the underlying values change (keeps OnGUI allocation-light).
    private int _cachedFloor = int.MinValue;
    private int _cachedCap = int.MinValue;
    private int _cachedDifficulty = int.MinValue;
    private string _floorLine = "";
    private string _capLine = "";

    private void Awake()
    {
        Log = base.Logger;
        Instance = this;
        AllowRaisingCap = base.Config.Bind("General", "AllowRaisingCap", true,
            "Show the 'Set unlocked cap to target' button. It raises your highest unlocked Mijing floor for the CURRENT difficulty by calling the game's own MijingManager.SetUnlockedFloorByCurrentDifficultyMax - this writes save progression (it can only raise the cap, never lower it).");
        Log.LogInfo("Mijing Floor Selector loaded. Open it from the 'Mods' menu on the right screen "
            + "edge; the window only appears where the Mijing system exists (home town / Mijing floors).");
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
    }

    // ---- Mods-menu surface (replaces the removed F10 hotkey) ------------------------------

    /// <summary>Window visibility, for the "Mods" menu row state.</summary>
    internal bool WindowVisible
    {
        get { return _show; }
    }

    /// <summary>
    /// Shows/hides the window - byte-for-byte the behavior the F10 handler had: a toggle also
    /// clears the one-shot _broken latch (so a single bad frame does not cost the tool for the
    /// rest of the session), and showing resets the status line, un-ticks the cap-raise confirm
    /// box and re-syncs the target floor to the floor you are standing on.
    /// </summary>
    internal void ToggleWindow()
    {
        if (_broken)
        {
            _broken = false;
            Log.LogWarning("Mijing Floor Selector re-enabled after a previous error.");
        }
        _show = !_show;
        if (_show)
        {
            _status = "";
            _confirmCapRaise = false;
            SyncTargetToCurrentFloor();
        }
    }

    private void OnGUI()
    {
        // The Mijing manager is scene-scoped: it only exists in the home town and inside levels.
        if (!_show || _broken || !SingletonMonoScope<MijingManager>.HasInstance)
        {
            _layoutArmed = false;
            return;
        }
        // The window is now opened from the Mods menu, i.e. from inside ANOTHER plugin's OnGUI
        // pass, so _show can flip in the middle of an input event and this window's very first
        // draw would land on that event. GUILayout requires a window's first event to be
        // EventType.Layout - otherwise it reads a layout cache that was never built and throws
        // "Getting control 0's position in a group with only 0 controls". Wait for the Layout
        // event (same frame, next OnGUI call), then draw normally from then on.
        if (!_layoutArmed)
        {
            if (Event.current.type != EventType.Layout)
            {
                return;
            }
            _layoutArmed = true;
        }
        _rect = GUILayout.Window(WindowId, _rect, DrawWindow, "Mijing Floor Selector");
        _rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, Screen.width - _rect.width));
        _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, Screen.height - _rect.height));
    }

    private void DrawWindow(int id)
    {
        try
        {
            MijingManager mm = SingletonMonoScope<MijingManager>.Instance;
            if (!mm)
            {
                GUILayout.Label("Mijing manager unavailable.");
                GUI.DragWindow();
                return;
            }

            int currentFloor = mm.GetCurrentFloor();
            int cap = mm.GetUnlockedFloorByCurrentDifficulty();
            RefreshCachedLines(mm, currentFloor, cap);

            GUILayout.BeginVertical();
            GUILayout.Label(_floorLine);
            GUILayout.Label(_capLine);

            GUILayout.Space(4f);

            // Target floor row: -10 / -1 / [field] / +1 / +10
            GUILayout.BeginHorizontal();
            GUILayout.Label("Target floor:", GUILayout.Width(80f));
            if (GUILayout.Button("-10", GUILayout.Width(38f))) { SetTargetFloor(_targetFloor - 10); }
            if (GUILayout.Button("-1", GUILayout.Width(32f))) { SetTargetFloor(_targetFloor - 1); }
            string newText = GUILayout.TextField(_targetFloorText, 6, GUILayout.Width(56f));
            if (!string.Equals(newText, _targetFloorText, StringComparison.Ordinal))
            {
                _targetFloorText = newText;
                if (int.TryParse(newText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int typed))
                {
                    _targetFloor = Mathf.Clamp(typed, 1, MaxTargetFloor);
                    if (_targetFloor != typed)
                    {
                        // Out-of-range input: snap the visible text to the effective value
                        // so the field can never show a number the buttons won't act on.
                        _targetFloorText = _targetFloor.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }
            // While the text does not round-trip to the effective target (mid-typing,
            // cleared field, non-numeric), the action buttons below stay disabled so
            // they can never act on a hidden value different from what is displayed.
            bool textInSync = int.TryParse(_targetFloorText, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int shown) && shown == _targetFloor;
            if (GUILayout.Button("+1", GUILayout.Width(32f))) { SetTargetFloor(_targetFloor + 1); }
            if (GUILayout.Button("+10", GUILayout.Width(38f))) { SetTargetFloor(_targetFloor + 10); }
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);

            // Enter button. Floor is clamped to [1, cap]: entering above the cap is only possible
            // after the user explicitly raises the cap below.
            string reason = GetEnterBlockReason(mm);
            if (reason == null && !textInSync)
            {
                reason = "Finish typing a valid floor number first.";
            }
            bool wasEnabled = GUI.enabled;
            GUI.enabled = reason == null;
            if (GUILayout.Button("Enter floor", GUILayout.Width(120f)))
            {
                EnterFloor(mm, cap);
            }
            GUI.enabled = wasEnabled;
            if (reason != null)
            {
                Color prev = GUI.color;
                GUI.color = Color.gray;
                GUILayout.Label(reason);
                GUI.color = prev;
            }
            else if (_targetFloor > cap)
            {
                Color prev = GUI.color;
                GUI.color = Color.gray;
                GUILayout.Label("Target is above the unlocked cap - Enter will use the cap. Raise the cap first to go higher.");
                GUI.color = prev;
            }

            // Cap-raise section (writes save progression via the game's own API).
            if (AllowRaisingCap.Value)
            {
                GUILayout.Space(6f);
                GUILayout.BeginHorizontal();
                _confirmCapRaise = GUILayout.Toggle(_confirmCapRaise, "Confirm", GUILayout.Width(80f));
                bool prevEnabled = GUI.enabled;
                GUI.enabled = _confirmCapRaise && SaveManager.HasRuntime && textInSync;
                if (GUILayout.Button("Set unlocked cap to target", GUILayout.Width(190f)))
                {
                    RaiseCap(mm, cap);
                }
                GUI.enabled = prevEnabled;
                GUILayout.EndHorizontal();
                if (!SaveManager.HasRuntime)
                {
                    Color prev = GUI.color;
                    GUI.color = Color.gray;
                    GUILayout.Label(ReasonNoSave);
                    GUI.color = prev;
                }
            }

            if (!string.IsNullOrEmpty(_status))
            {
                GUILayout.Space(4f);
                GUILayout.Label(_status);
            }
            GUILayout.EndVertical();
            GUI.DragWindow();
        }
        catch (Exception ex)
        {
            // Fail soft: one error, then keep the window off instead of throwing every frame.
            _broken = true;
            _show = false;
            Log.LogError("Mijing Floor Selector window disabled after error: " + ex);
        }
    }

    // Mirrors the guards inside MijingManager.EnterMijingInternal so we never trigger its
    // error paths: home scene or an existing Mijing floor, ids loaded, not already loading.
    private static string GetEnterBlockReason(MijingManager mm)
    {
        if (!SaveManager.HasRuntime)
        {
            return ReasonNoSave;
        }
        if (!SaveManager.RuntimeData.UnlockedMijing)
        {
            return ReasonNotUnlocked;
        }
        if (mm.IsEnteringMijing)
        {
            return ReasonEntering;
        }
        if (MijingManager.mijingIds == null || MijingManager.mijingIds.Count == 0)
        {
            return ReasonNoIds;
        }
        if (SceneManager.GetActiveScene().name != "HomeScene" && !LevelManager.GetIsMijing())
        {
            return ReasonWrongScene;
        }
        return null;
    }

    private void EnterFloor(MijingManager mm, int cap)
    {
        try
        {
            int floor = Mathf.Clamp(_targetFloor, 1, Mathf.Max(1, cap));
            mm.EnterMijing(floor);
            _status = "Entering floor " + floor.ToString(CultureInfo.InvariantCulture)
                + " (" + DifficultyName(mm.CurrentDifficulty) + ")...";
            Log.LogInfo("MijingSelector: " + _status);
        }
        catch (Exception ex)
        {
            _status = "ERROR: " + ex.GetBaseException().Message;
            Log.LogError(ex);
        }
    }

    private void RaiseCap(MijingManager mm, int cap)
    {
        try
        {
            _confirmCapRaise = false;
            if (_targetFloor <= cap)
            {
                _status = "Cap already at or above target (the game's API never lowers it).";
                return;
            }
            mm.SetUnlockedFloorByCurrentDifficultyMax(_targetFloor);
            int newCap = mm.GetUnlockedFloorByCurrentDifficulty();
            _status = "Unlocked cap for " + DifficultyName(mm.CurrentDifficulty) + " is now "
                + newCap.ToString(CultureInfo.InvariantCulture) + ".";
            Log.LogInfo("MijingSelector: " + _status);
        }
        catch (Exception ex)
        {
            _status = "ERROR: " + ex.GetBaseException().Message;
            Log.LogError(ex);
        }
    }

    private void SetTargetFloor(int value)
    {
        _targetFloor = Mathf.Clamp(value, 1, MaxTargetFloor);
        _targetFloorText = _targetFloor.ToString(CultureInfo.InvariantCulture);
    }

    private void SyncTargetToCurrentFloor()
    {
        try
        {
            if (SingletonMonoScope<MijingManager>.HasInstance)
            {
                MijingManager mm = SingletonMonoScope<MijingManager>.Instance;
                if (mm)
                {
                    SetTargetFloor(mm.GetCurrentFloor());
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogError(ex);
        }
    }

    private void RefreshCachedLines(MijingManager mm, int currentFloor, int cap)
    {
        int difficulty = (int)mm.CurrentDifficulty;
        if (currentFloor == _cachedFloor && cap == _cachedCap && difficulty == _cachedDifficulty)
        {
            return;
        }
        _cachedFloor = currentFloor;
        _cachedCap = cap;
        _cachedDifficulty = difficulty;
        string diffName = DifficultyName(mm.CurrentDifficulty);
        _floorLine = "Difficulty: " + diffName + "   Current floor: "
            + currentFloor.ToString(CultureInfo.InvariantCulture);
        _capLine = "Unlocked cap (" + diffName + "): "
            + cap.ToString(CultureInfo.InvariantCulture);
    }

    private static string DifficultyName(DifficultType difficulty)
    {
        switch (difficulty)
        {
            case DifficultType.Easy: return "Easy";
            case DifficultType.Medium: return "Medium";
            case DifficultType.Hard: return "Hard";
            case DifficultType.Master: return "Master";
            default: return "Unknown";
        }
    }
}
