using System;
using System.Collections;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using FinkFramework.Runtime.Singleton;
using HarmonyLib;
using UI.Panels;
using UnityEngine;

namespace QuickEnhance;

// Quick Enhance: hold Shift while clicking in the weapon enhance panel to enhance
// repeatedly (one attempt per frame) until the weapon is at +max, the money runs
// out, or the configured iteration cap is reached - instead of one level per click.
//
// The game performs one enhancement per left click via the private method
// UI.Panels.WeaponManager.HandleEnhInput(), which WeaponManager.Update() calls every
// frame while GameUIManager.CurrentModalState == GlobalUiModalState.WeaponEnh.
// We prefix-patch HandleEnhInput: when Shift is held on the triggering click we
// suppress the vanilla single action and run a coroutine that re-checks the game's
// own guards (RefreshForgeContext(Enh) -> forgeContext.IsValid -> CanTryForgeEnh())
// and invokes TryRandomEnh() once per frame.
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "custom.quickenhance";
    public const string PluginName = "Quick Enhance";
    public const string PluginVersion = "1.0.0";

    internal static ManualLogSource Log;
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> RequireShift;
    internal static ConfigEntry<int> MaxIterationsPerBurst;

    private static Plugin _instance;
    private static bool _featureReady;
    private static bool _loopRunning;

    // Reflection handles for WeaponManager's private enhance internals.
    // All resolved ONCE in Awake; if anything is missing the feature stays disabled
    // (single warning, no patch, no per-frame work).
    private static MethodInfo _miRefreshForgeContext; // private void RefreshForgeContext(WeaponForgeMode)
    private static object[] _refreshEnhArgs;          // cached { boxed WeaponForgeMode.Enh } - reused, no per-call alloc
    private static FieldInfo _fiForgeContext;         // private readonly WeaponForgeContext forgeContext
    private static FieldInfo _fiCtxIsValid;           // WeaponForgeContext.IsValid (bool)
    private static FieldInfo _fiCtxRuntimeWeapon;     // WeaponForgeContext.RuntimeWeapon (WeaponClass)
    private static Func<WeaponManager, bool> _canTryForgeEnh;      // private bool CanTryForgeEnh()
    private static Action<WeaponManager> _tryRandomEnh;            // private void TryRandomEnh()
    private static Func<WeaponClass, int> _getRemainEnhanceCount;  // private static int GetRemainEnhanceCount(WeaponClass)
    private static Func<bool> _isSubmitDown;                       // private static bool IsSubmitDown()

    private Harmony _harmony;

    private void Awake()
    {
        _instance = this;
        Log = base.Logger;

        Enabled = base.Config.Bind("QuickEnhance", "Enabled", true,
            "Master switch. When false the plugin never intercepts the enhance panel and any running burst stops immediately.");
        RequireShift = base.Config.Bind("QuickEnhance", "RequireShift", true,
            "When true (default), only clicks made while holding Left or Right Shift start a repeated-enhance burst; a plain click enhances once, exactly like vanilla. When false, EVERY enhance click starts a burst.");
        MaxIterationsPerBurst = base.Config.Bind("QuickEnhance", "MaxIterationsPerBurst", 40,
            new ConfigDescription(
                "Safety cap on enhance attempts per Shift-click burst (one attempt per frame). The burst also stops early when the weapon reaches its +max, you run out of money, the panel closes, or an attempt makes no progress.",
                new AcceptableValueRange<int>(1, 500)));

        try
        {
            MethodInfo miHandleEnhInput = AccessTools.Method(typeof(WeaponManager), "HandleEnhInput");
            MethodInfo miCanTry = AccessTools.Method(typeof(WeaponManager), "CanTryForgeEnh");
            MethodInfo miTryEnh = AccessTools.Method(typeof(WeaponManager), "TryRandomEnh");
            MethodInfo miRemain = AccessTools.Method(typeof(WeaponManager), "GetRemainEnhanceCount");
            MethodInfo miSubmit = AccessTools.Method(typeof(WeaponManager), "IsSubmitDown");
            Type forgeModeType = AccessTools.Inner(typeof(WeaponManager), "WeaponForgeMode");
            _miRefreshForgeContext = AccessTools.Method(typeof(WeaponManager), "RefreshForgeContext");
            _fiForgeContext = AccessTools.Field(typeof(WeaponManager), "forgeContext");

            if (miHandleEnhInput == null || miCanTry == null || miTryEnh == null || miRemain == null
                || miSubmit == null || forgeModeType == null || _miRefreshForgeContext == null || _fiForgeContext == null)
            {
                Log.LogWarning("Quick Enhance: could not resolve WeaponManager enhance internals (game updated?). Feature disabled.");
                return;
            }

            Type ctxType = _fiForgeContext.FieldType;
            _fiCtxIsValid = AccessTools.Field(ctxType, "IsValid");
            _fiCtxRuntimeWeapon = AccessTools.Field(ctxType, "RuntimeWeapon");
            if (_fiCtxIsValid == null || _fiCtxRuntimeWeapon == null)
            {
                Log.LogWarning("Quick Enhance: could not resolve WeaponForgeContext fields (game updated?). Feature disabled.");
                return;
            }

            _refreshEnhArgs = new object[] { Enum.Parse(forgeModeType, "Enh") };
            _canTryForgeEnh = (Func<WeaponManager, bool>)Delegate.CreateDelegate(typeof(Func<WeaponManager, bool>), miCanTry);
            _tryRandomEnh = (Action<WeaponManager>)Delegate.CreateDelegate(typeof(Action<WeaponManager>), miTryEnh);
            _getRemainEnhanceCount = (Func<WeaponClass, int>)Delegate.CreateDelegate(typeof(Func<WeaponClass, int>), miRemain);
            _isSubmitDown = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), miSubmit);

            _harmony = new Harmony(PluginGuid);
            _harmony.Patch(miHandleEnhInput, prefix: new HarmonyMethod(typeof(Plugin), nameof(HandleEnhInputPrefix)));
            _featureReady = true;
            Log.LogInfo("Quick Enhance loaded: hold Shift and click in the enhance panel to enhance repeatedly.");
        }
        catch (Exception ex)
        {
            _featureReady = false;
            Log.LogWarning("Quick Enhance failed to initialize, feature disabled: " + ex.Message);
        }
    }

    private void OnDestroy()
    {
        _loopRunning = false;
        _featureReady = false;
        _harmony?.UnpatchSelf();
    }

    // WeaponManager.Update() calls HandleEnhInput() every frame while the enhance
    // modal is open, so this prefix must stay cheap and allocation-free on the
    // idle path (a few bool/config/key checks only).
    private static bool HandleEnhInputPrefix(WeaponManager __instance)
    {
        if (!_featureReady || !Enabled.Value)
        {
            return true; // vanilla behaviour
        }
        if (_loopRunning)
        {
            return false; // suppress vanilla single-enhance while a burst is in flight
        }
        if (RequireShift.Value && !Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift))
        {
            return true; // plain click -> vanilla single enhance
        }
        try
        {
            // Same gates the original method checks before acting.
            if (!IsEnhModalOpen())
            {
                return true;
            }
            if (!_isSubmitDown())
            {
                return true; // no click this frame - vanilla no-ops anyway
            }
            // Shift-click (or any click with RequireShift=false): start the burst.
            // StartCoroutine runs up to the first yield synchronously, so the first
            // enhancement lands this very frame, same responsiveness as vanilla.
            _instance.StartCoroutine(_instance.EnhanceBurst(__instance));
            return false;
        }
        catch (Exception ex)
        {
            DisableFeature("Quick Enhance: prefix failed, feature disabled: " + ex.Message);
            return true;
        }
    }

    private IEnumerator EnhanceBurst(WeaponManager wm)
    {
        _loopRunning = true;
        int attempts = 0;
        int successes = 0;
        int startLevel = -1;
        int endLevel = -1;
        int cap = Mathf.Max(1, MaxIterationsPerBurst.Value);
        try
        {
            while (attempts < cap)
            {
                // Stop immediately if the plugin is disabled, the panel/scene went
                // away, or the modal state changed (panel closed / mode switched).
                if (!_featureReady || !Enabled.Value || !wm || !IsEnhModalOpen())
                {
                    break;
                }
                attempts++;
                if (!TryEnhanceOnce(wm, ref startLevel, ref endLevel))
                {
                    break;
                }
                successes++;
                yield return null; // one enhancement per frame
            }
        }
        finally
        {
            _loopRunning = false;
        }
        if (successes > 0 && startLevel >= 0)
        {
            Log.LogInfo($"Quick Enhance: from +{startLevel} to +{endLevel} ({successes} enhancement(s), {attempts} attempt(s)).");
        }
        else
        {
            Log.LogInfo($"Quick Enhance: nothing enhanced ({attempts} attempt(s)) - the game's tip shows the reason (max reached / not enough money / no weapon selected).");
        }
    }

    // One guarded enhance attempt, mirroring HandleEnhInput exactly:
    // RefreshForgeContext(Enh) -> forgeContext.IsValid -> CanTryForgeEnh() -> TryRandomEnh().
    // Returns true only when the weapon's +level actually increased; any failure or
    // lack of progress ends the burst. Never called outside the WeaponEnh modal state.
    private static bool TryEnhanceOnce(WeaponManager wm, ref int startLevel, ref int endLevel)
    {
        try
        {
            _miRefreshForgeContext.Invoke(wm, _refreshEnhArgs);
            object ctx = _fiForgeContext.GetValue(wm);
            if (ctx == null || !(bool)_fiCtxIsValid.GetValue(ctx))
            {
                return false;
            }
            WeaponClass weapon = _fiCtxRuntimeWeapon.GetValue(ctx) as WeaponClass;
            if (weapon == null)
            {
                return false;
            }
            // CanTryForgeEnh covers hand-item, remaining-count and money guards and
            // shows the vanilla fail tip once on the terminating attempt.
            if (!_canTryForgeEnh(wm))
            {
                return false;
            }
            if (_getRemainEnhanceCount(weapon) <= 0)
            {
                return false; // belt and braces - CanTryForgeEnh already checks this
            }
            int before = weapon.ZQ_CountMax;
            if (startLevel < 0)
            {
                startLevel = before;
            }
            _tryRandomEnh(wm);
            endLevel = weapon.ZQ_CountMax;
            // TryRandomEnh can silently do nothing (e.g. weapon with no enhanceable
            // stat, or clone failure). No progress -> stop instead of spinning.
            return endLevel > before;
        }
        catch (Exception ex)
        {
            DisableFeature("Quick Enhance: reflected enhance call failed, feature disabled: " + ex.Message);
            return false;
        }
    }

    private static bool IsEnhModalOpen()
    {
        return SingletonMonoScope<GameUIManager>.HasInstance
            && SingletonMonoScope<GameUIManager>.Instance.CurrentModalState == GlobalUiModalState.WeaponEnh;
    }

    private static void DisableFeature(string reason)
    {
        if (_featureReady)
        {
            _featureReady = false;
            Log.LogWarning(reason);
        }
    }
}
