using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PerfPatches;

/// <summary>
/// Performance patch suite for Shadow Dungeon. Every patch is an independent module with its
/// own config toggle; a module that fails to install (or throws at runtime) disables itself
/// and never takes the plugin or the game down. Modules live in Modules/*.cs and are invoked
/// through <see cref="PerfCore"/> callbacks so only this class is a MonoBehaviour.
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "custom.perfpatches";
    public const string PluginName = "Performance Patches";
    public const string PluginVersion = "1.0.2";

    private Harmony _harmony;

    private void Awake()
    {
        PerfCore.Log = base.Logger;
        _harmony = new Harmony(PluginGuid);

        // Module registry: ONLY reviewed modules. Seven further modules were generated beyond
        // the reviewed scope (graphics, loading, memory, save-system, interactables, skill
        // refresh, misc hot paths); they live outside this project in
        // ShadowDungeonSaveTool/_quarantine-perfpatches-unreviewed/ and must not be added back
        // without a line-by-line review - one of them patches save writing.
        PerfCore.InitModule("PlayerPhysics", () => PlayerPhysicsModule.Init(Config, _harmony));
        PerfCore.InitModule("Fields", () => FieldsModule.Init(Config, _harmony));
        PerfCore.InitModule("Projectiles", () => ProjectilesModule.Init(Config, _harmony));
        PerfCore.InitModule("EnemyAi", () => EnemyAiModule.Init(Config, _harmony));
        PerfCore.InitModule("EnemyAiLod", () => EnemyAiLodModule.Init(Config, _harmony));
        PerfCore.InitModule("UiFeedback", () => UiFeedbackModule.Init(Config, _harmony));
        PerfCore.InitModule("Engine", () => EngineModule.Init(Config, _harmony));
        PerfCore.InitModule("Overlay", () => OverlayModule.Init(Config, _harmony));

        SceneManager.sceneUnloaded += OnSceneUnloaded;
        PerfCore.TraceDone(PerfCore.Summary());
        PerfCore.Log.LogInfo("Performance Patches loaded: " + PerfCore.Summary());
    }

    private void OnDestroy()
    {
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
        _harmony?.UnpatchSelf();
    }

    private static void OnSceneUnloaded(Scene scene)
    {
        PerfCore.RaiseSceneUnloaded();
    }

    private void Update() => PerfCore.RaiseUpdate();

    private void LateUpdate() => PerfCore.RaiseLateUpdate();

    private void OnGUI() => PerfCore.RaiseGui();
}

/// <summary>
/// Shared plumbing for the patch modules: logging, frame callbacks with per-callback
/// fail-soft disable, and scene-unload cache invalidation.
/// </summary>
internal static class PerfCore
{
    internal static ManualLogSource Log;

    private sealed class Hook
    {
        public string Owner;
        public Action Action;
        public bool Dead;
    }

    private static readonly List<Hook> UpdateHooks = new List<Hook>();
    private static readonly List<Hook> LateUpdateHooks = new List<Hook>();
    private static readonly List<Hook> GuiHooks = new List<Hook>();
    private static readonly List<Hook> SceneUnloadHooks = new List<Hook>();
    private static readonly List<string> Loaded = new List<string>();
    private static readonly List<string> Failed = new List<string>();

    // Startup breadcrumb file. BepInEx's disk log is buffered, so a HARD runtime crash (a
    // StackOverflowException is not catchable and takes the process down instantly) loses the
    // lines that would identify the culprit - exactly what happened when an early revision of
    // EngineModule enumerated every loaded assembly's types. These writes flush immediately, so
    // whatever module is named last in the trace is the one that died. ~15 tiny writes, startup
    // only, and any IO failure is ignored.
    private static string _tracePath;

    private static void Trace(string line)
    {
        try
        {
            if (_tracePath == null)
            {
                string dir = System.IO.Path.Combine(Paths.PluginPath, "PerfBench");
                System.IO.Directory.CreateDirectory(dir);
                _tracePath = System.IO.Path.Combine(dir, "init-trace.log");
                System.IO.File.WriteAllText(_tracePath,
                    "PerfPatches " + Plugin.PluginVersion + " init trace (newest run)\n");
            }
            System.IO.File.AppendAllText(_tracePath, line + "\n");
        }
        catch
        {
            _tracePath = null; // never let diagnostics break startup
        }
    }

    internal static void InitModule(string name, Action init)
    {
        Trace("-> " + name);
        try
        {
            init();
            Loaded.Add(name);
            Trace("   ok " + name);
        }
        catch (Exception ex)
        {
            Failed.Add(name);
            Trace("   FAILED " + name + ": " + ex.Message);
            Log.LogError("PerfPatches module '" + name + "' failed to initialize and is disabled: " + ex);
        }
    }

    internal static void TraceDone(string summary)
    {
        Trace("== all modules processed: " + summary);
    }

    internal static string Summary()
    {
        return string.Join(", ", Loaded) + (Failed.Count > 0 ? " | FAILED: " + string.Join(", ", Failed) : "");
    }

    internal static void OnUpdate(string owner, Action action) => UpdateHooks.Add(new Hook { Owner = owner, Action = action });
    internal static void OnLateUpdate(string owner, Action action) => LateUpdateHooks.Add(new Hook { Owner = owner, Action = action });
    internal static void OnGui(string owner, Action action) => GuiHooks.Add(new Hook { Owner = owner, Action = action });
    internal static void OnSceneUnloaded(string owner, Action action) => SceneUnloadHooks.Add(new Hook { Owner = owner, Action = action });

    internal static void RaiseUpdate() => Raise(UpdateHooks);
    internal static void RaiseLateUpdate() => Raise(LateUpdateHooks);
    internal static void RaiseGui() => Raise(GuiHooks);
    internal static void RaiseSceneUnloaded() => Raise(SceneUnloadHooks);

    private static void Raise(List<Hook> hooks)
    {
        for (int i = 0; i < hooks.Count; i++)
        {
            Hook h = hooks[i];
            if (h.Dead)
            {
                continue;
            }
            try
            {
                h.Action();
            }
            catch (Exception ex)
            {
                h.Dead = true;
                Log.LogError("PerfPatches: '" + h.Owner + "' frame hook disabled after error: " + ex);
            }
        }
    }

    /// <summary>Main key pressed this frame + modifiers held. Never use KeyboardShortcut.IsDown
    /// for combat hotkeys - it rejects the press while any unrelated key (WASD) is held.
    /// NOTE: currently UNUSED - OverlayModule's F8 / Shift+F8 bindings were removed when the
    /// floating "Mods" menu became the single point of interaction. Kept deliberately as the
    /// house helper for any future module that needs a key, so the correct implementation does
    /// not have to be rediscovered.</summary>
    internal static bool HotkeyPressed(KeyboardShortcut shortcut)
    {
        if (shortcut.MainKey == KeyCode.None || !Input.GetKeyDown(shortcut.MainKey))
        {
            return false;
        }
        foreach (KeyCode modifier in shortcut.Modifiers)
        {
            if (!Input.GetKey(modifier))
            {
                return false;
            }
        }
        return true;
    }
}
