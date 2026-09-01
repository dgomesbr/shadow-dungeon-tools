using System;
using System.Globalization;
using UnityEngine.SceneManagement;
using UnityEngine;
using System.Text;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine.UI;

namespace ReadableNumbers;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "custom.readablenumbers";
    public const string PluginName = "Readable Numbers";
    public const string PluginVersion = "1.2.1";

    public enum DamageFormatMode
    {
        /// <summary>Nearest named scale unit: "510 Billion", "1.2 Trillion", "3.4 Quadrillion";
        /// full grouped integer below one million.</summary>
        NamedUnits,

        /// <summary>Grouped mantissa + short unit, capped at B so grouping actually engages (1.2345e12 -> "1,234.5 B").</summary>
        GroupedSuffix,

        /// <summary>Full grouped integer below one billion ("43,083,369" style), grouped mantissa + unit at or above 1e9.</summary>
        FullBelowBillion,

        /// <summary>Full grouped integer always ("43,083,369,558"). Long strings - combat text labels may overflow.</summary>
        FullAlways
    }

    internal static ManualLogSource Log;
    internal static ConfigEntry<DamageFormatMode> Mode;
    internal static ConfigEntry<bool> FormatMoney;
    internal static ConfigEntry<bool> FixSciNotation;
    internal static ConfigEntry<bool> SweepAllUiText;

    // Cached invariant culture: group separator is ','. Format strings are consts, so the
    // per-call cost is one unavoidable result-string allocation - same as the vanilla method.
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string GroupedInt = "#,##0";
    private const string GroupedOneDecimal = "#,##0.#";

    // Fail-soft latches: after the first runtime error we log once and permanently fall back
    // to the game's own formatting instead of throwing on every combat-text spawn.
    private static bool _damageFormatBroken;
    private static bool _moneyFormatBroken;

    // Set only by the "Mods" side menu row (numbers.mode). Default false = existing behaviour.
    // When true the damage-format prefix falls straight through to the vanilla method; the menu
    // row clears FixSciNotation at the same time, so the UI sweep stops too.
    internal static bool VanillaNumbersOverride;

    private Harmony _harmony;

    private void Awake()
    {
        Log = base.Logger;

        Mode = base.Config.Bind("Damage", "Mode", DamageFormatMode.NamedUnits,
            "How big damage numbers (combat text + DPS meter) are formatted. " +
            "NamedUnits: nearest named scale - '510 Billion', '1.2 Trillion', '3.4 Quadrillion' (full grouped integer below one million). " +
            "GroupedSuffix: grouped mantissa with a short unit, capped at B so trillions read as '1,234.5 B'. " +
            "FullBelowBillion: full grouped integer (e.g. '843,083,369') for values below 1,000,000,000, GroupedSuffix style above. " +
            "FullAlways: full grouped integer for every value (e.g. '43,083,369,558') - beware combat-text label overflow. " +
            "Values of 1000 or less always keep the vanilla plain formatting.");

        SweepAllUiText = base.Config.Bind("UI", "SweepAllUiText", false,
            "Extra safety net: once every 0.1s, scan every live UI label and rewrite any that " +
            "shows scientific notation. The HP and mana readouts are already handled precisely " +
            "by patching the method that writes them, so this is only needed if you spot 'E+' " +
            "somewhere else. Costs a periodic scan of all Text components, hence off by default.");

        FixSciNotation = base.Config.Bind("UI", "FixScientificNotationText", true,
            "Fixes the HP and mana readouts, which the game writes as raw floats and therefore shows as " +
            "'1.035056E+11/1.035056E+11' at high values, rendering them in the same named-scale form " +
            "used for damage ('103.5 Billion/103.5 Billion'). Implemented by replacing the write in " +
            "TooltipItem.RefreshUI, so exactly one value is written per frame and the label cannot " +
            "flicker between formatted and raw text. Also the master switch for SweepAllUiText.");

        FormatMoney = base.Config.Bind("Money", "FormatMoney", true,
            "Also apply thousands separators to the gold counter in the inventory UI " +
            "(the game shows raw digits like 184039201). Shop/forge price labels are NOT " +
            "touched - see README for why.");

        _harmony = new Harmony(PluginGuid);
        PatchDamageFormatter();
        PatchMoneyText();
        PatchStatReadouts();
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
    }

    private void PatchDamageFormatter()
    {
        try
        {
            // public static string DamgeTextManager.FormatDamageNumber(float number)
            // DPSManager.FormatDamageNumber(float) delegates to it, so the DPS meter is covered too.
            MethodInfo target = AccessTools.Method(typeof(DamgeTextManager),
                nameof(DamgeTextManager.FormatDamageNumber), new[] { typeof(float) });
            if (target == null)
            {
                Log.LogWarning("Readable Numbers: DamgeTextManager.FormatDamageNumber(float) not found - damage formatting disabled.");
                return;
            }
            _harmony.Patch(target, prefix: new HarmonyMethod(typeof(Plugin), nameof(FormatDamageNumberPrefix)));
            Log.LogInfo("Readable Numbers: patched DamgeTextManager.FormatDamageNumber (mode: " + Mode.Value + ").");
        }
        catch (Exception ex)
        {
            Log.LogWarning("Readable Numbers: failed to patch damage formatting, feature disabled. " + ex.Message);
        }
    }

    private void PatchMoneyText()
    {
        try
        {
            // The gold counter label (InventoryManager.moneyText) is written from exactly two
            // places, both inside InventoryManager: the GlobalMoney property setter and Start().
            MethodInfo setter = AccessTools.PropertySetter(typeof(InventoryManager), nameof(InventoryManager.GlobalMoney));
            MethodInfo start = AccessTools.Method(typeof(InventoryManager), "Start");
            if (setter == null || start == null)
            {
                Log.LogWarning("Readable Numbers: InventoryManager.GlobalMoney setter or Start() not found - money formatting disabled.");
                return;
            }
            HarmonyMethod postfix = new HarmonyMethod(typeof(Plugin), nameof(MoneyTextPostfix));
            _harmony.Patch(setter, postfix: postfix);
            _harmony.Patch(start, postfix: postfix);
            Log.LogInfo("Readable Numbers: patched InventoryManager gold counter (set_GlobalMoney + Start).");
        }
        catch (Exception ex)
        {
            Log.LogWarning("Readable Numbers: failed to patch money formatting, feature disabled. " + ex.Message);
        }
    }

    // Prefix on DamgeTextManager.FormatDamageNumber(float): set __result and skip the original.
    // Returning true falls through to the vanilla method, which preserves its exact behavior for
    // zero/negative ("0"), small (<= 1000 -> plain floor) and NaN/Infinity inputs.
    private static bool FormatDamageNumberPrefix(float number, ref string __result)
    {
        if (_damageFormatBroken || VanillaNumbersOverride)
        {
            return true;
        }
        if (!(number > 1000f) || float.IsNaN(number) || float.IsInfinity(number))
        {
            return true;
        }
        try
        {
            __result = FormatBig(number, Mode.Value);
            return false;
        }
        catch (Exception ex)
        {
            _damageFormatBroken = true;
            Log.LogError("Readable Numbers: damage formatting failed, reverting to vanilla formatting. " + ex);
            return true;
        }
    }

    // Short-scale ladder covering the whole float range (float.MaxValue ~ 3.4e38).
    // Index i corresponds to 10^(6 + 3*i).
    private static readonly string[] ScaleNames =
    {
        " Million", " Billion", " Trillion", " Quadrillion", " Quintillion",
        " Sextillion", " Septillion", " Octillion", " Nonillion", " Decillion", " Undecillion"
    };

    private static string FormatBig(float number, DamageFormatMode mode)
    {
        // Work in double: float carries only ~7 significant digits, and legacy Mono fixed-point
        // formatting of a float rounds even harder. Casting up keeps every digit the float has.
        double value = number;
        if (mode == DamageFormatMode.FullAlways || (mode == DamageFormatMode.FullBelowBillion && value < 1e9))
        {
            return Math.Floor(value).ToString(GroupedInt, Inv);
        }

        if (mode == DamageFormatMode.NamedUnits)
        {
            if (value < 1e6)
            {
                return Math.Floor(value).ToString(GroupedInt, Inv);
            }
            // Largest scale whose threshold the value reaches; mantissa stays in [1, 1000)
            // except past the ladder's end, where grouping keeps it readable anyway.
            int idx = (int)Math.Floor(Math.Log10(value) / 3.0) - 2;
            if (idx >= ScaleNames.Length)
            {
                idx = ScaleNames.Length - 1;
            }
            double named = value / Math.Pow(10.0, 6 + 3 * idx);
            if (named < 1.0 && idx > 0)
            {
                // Log10 edge (e.g. 999,999,999.9 landing just under the boundary): step down.
                idx--;
                named = value / Math.Pow(10.0, 6 + 3 * idx);
            }
            return named.ToString(GroupedOneDecimal, Inv) + ScaleNames[idx];
        }

        // GroupedSuffix: ladder is capped at B (1e9) on purpose - with the vanilla K..Y ladder the
        // mantissa never exceeds 3 digits and thousands separators would never appear. Capping at B
        // makes trillions read as "1,234.5 B", quadrillions as "1,234,500 B".
        double mantissa;
        string unit;
        if (value < 1e6)
        {
            mantissa = value / 1e3;
            unit = " K";
        }
        else if (value < 1e9)
        {
            mantissa = value / 1e6;
            unit = " M";
        }
        else
        {
            mantissa = value / 1e9;
            unit = " B";
        }
        return mantissa.ToString(GroupedOneDecimal, Inv) + unit;
    }

    // Postfix on InventoryManager.set_GlobalMoney and InventoryManager.Start(): both vanilla
    // bodies end with moneyText.text = GlobalMoney.ToString(), so we simply overwrite the label
    // with the grouped rendering afterwards. Money is a long, so no precision loss.
    private static void MoneyTextPostfix(InventoryManager __instance)
    {
        if (_moneyFormatBroken || !FormatMoney.Value)
        {
            return;
        }
        try
        {
            Text label = __instance.moneyText;
            if (label)
            {
                label.text = __instance.GlobalMoney.ToString(GroupedInt, Inv);
            }
        }
        catch (Exception ex)
        {
            _moneyFormatBroken = true;
            Log.LogError("Readable Numbers: money formatting failed, reverting to vanilla gold text. " + ex);
        }
    }

    // ---- HP / mana readouts (TooltipItem) -------------------------------------------------
    // Verified write site: TooltipItem.RefreshUI() assigns HealthText.text and ManaText.text as
    // string.Format("{0}/{1}", floorCur, floorMax) every frame from Update(), so past ~1e7 the
    // floats render as "1.035056E+11".
    //
    // This MUST be a prefix that replaces the body, not a postfix. Vanilla rewrites both labels
    // unconditionally every frame, so a postfix that skipped unchanged values let the raw
    // scientific-notation string stand on every frame where HP/mana did not move - the text
    // visibly flickered between "103.5 Billion" and "1.035056E+11". Owning the write means the
    // value-change cache is safe: when nothing changed, nobody writes at all.
    private static readonly Dictionary<int, Vector4> TipCache = new Dictionary<int, Vector4>(8);
    private static bool _tipBroken;
    // Plain FieldInfo: at most a handful of reads per frame, and it avoids the ambiguous
    // AccessTools.FieldRefAccess overloads for weakly-typed (object, object) access.
    private static FieldInfo _tipHealthText;
    private static FieldInfo _tipManaText;
    private static FieldInfo _tipHealthStat;
    private static FieldInfo _tipManaStat;
    private static FieldInfo _statCurField;
    private static PropertyInfo _statMax;

    private void PatchStatReadouts()
    {
        try
        {
            Type tip = AccessTools.TypeByName("TooltipItem");
            MethodInfo refresh = tip != null ? AccessTools.Method(tip, "RefreshUI") : null;
            if (refresh == null)
            {
                Log.LogWarning("Readable Numbers: TooltipItem.RefreshUI not found - HP/mana readouts left as-is.");
                return;
            }
            Type statType = AccessTools.TypeByName("Stat");
            _tipHealthText = AccessTools.Field(tip, "HealthText");
            _tipManaText = AccessTools.Field(tip, "ManaText");
            _tipHealthStat = AccessTools.Field(tip, "healthStat");
            _tipManaStat = AccessTools.Field(tip, "ManaStat");
            _statCurField = statType != null ? AccessTools.Field(statType, "Cur") : null;
            _statMax = statType != null ? AccessTools.Property(statType, "Max") : null;
            if (_statMax == null || _statCurField == null || _tipHealthText == null
                || _tipManaText == null || _tipHealthStat == null || _tipManaStat == null)
            {
                Log.LogWarning("Readable Numbers: TooltipItem/Stat members not found - HP/mana readouts left as-is.");
                return;
            }
            _harmony.Patch(refresh, prefix: new HarmonyMethod(typeof(Plugin), nameof(RefreshUIPrefix)));
            Log.LogInfo("Readable Numbers: patched TooltipItem.RefreshUI (HP/mana readouts).");
        }
        catch (Exception ex)
        {
            Log.LogWarning("Readable Numbers: failed to patch HP/mana readouts: " + ex.Message);
        }
    }

    // Verbatim port of TooltipItem.RefreshUI (TooltipItem.cs:37-52): same activeSelf guards,
    // same Mathf.Floor on Max/Cur, same Clamp(cur, 0, max) - only the string differs, plus the
    // change check that skips redundant canvas writes.
    private static bool RefreshUIPrefix(object __instance)
    {
        if (_tipBroken)
        {
            return true; // fall back to vanilla formatting for the rest of the session
        }
        if (VanillaNumbersOverride || FixSciNotation == null || !FixSciNotation.Value)
        {
            return true; // user asked for vanilla numbers
        }
        try
        {
            Component comp = __instance as Component;
            if (!comp)
            {
                return true;
            }
            int id = comp.GetInstanceID();
            Vector4 last;
            if (!TipCache.TryGetValue(id, out last))
            {
                last = new Vector4(float.NaN, float.NaN, float.NaN, float.NaN);
            }

            object healthStat = _tipHealthStat.GetValue(__instance);
            Text healthText = _tipHealthText.GetValue(__instance) as Text;
            if (healthStat != null && (bool)(Component)healthStat && healthText && healthText.gameObject.activeSelf)
            {
                float max = Mathf.Floor((float)_statMax.GetValue(healthStat, null));
                float cur = Mathf.Clamp(Mathf.Floor((float)_statCurField.GetValue(healthStat)), 0f, max);
                if (cur != last.x || max != last.y)
                {
                    last.x = cur;
                    last.y = max;
                    healthText.text = FormatNamed(cur) + "/" + FormatNamed(max);
                }
            }

            object manaStat = _tipManaStat.GetValue(__instance);
            Text manaText = _tipManaText.GetValue(__instance) as Text;
            if (manaStat != null && (bool)(Component)manaStat && manaText && manaText.gameObject.activeSelf)
            {
                float max = Mathf.Floor((float)_statMax.GetValue(manaStat, null));
                float cur = Mathf.Clamp(Mathf.Floor((float)_statCurField.GetValue(manaStat)), 0f, max);
                if (cur != last.z || max != last.w)
                {
                    last.z = cur;
                    last.w = max;
                    manaText.text = FormatNamed(cur) + "/" + FormatNamed(max);
                }
            }
            TipCache[id] = last;
            return false; // we own both labels this frame
        }
        catch (Exception ex)
        {
            _tipBroken = true;
            Log.LogError("Readable Numbers: HP/mana readout formatting disabled after error: " + ex);
            return true;
        }
    }

    // ---- scientific-notation repair on UI text -------------------------------------------
    // Some readouts (player HP/mana) are written with a plain float ToString(), which switches
    // to "1.035056E+11" past ~1e7. Rather than guess which script owns each label, sweep the
    // live Text components and rewrite only the ones currently showing E notation. The cache is
    // rebuilt on scene load and refreshed periodically so labels created later are covered.
    private static readonly List<Text> TextCache = new List<Text>(256);
    private float _nextSweepAt;
    private float _nextRescanAt;
    private bool _sweepBroken;
    private static readonly StringBuilder SciSb = new StringBuilder(64);

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoadedForSweep;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoadedForSweep;
    }

    private void OnSceneLoadedForSweep(Scene scene, LoadSceneMode mode)
    {
        TextCache.Clear();
        _nextRescanAt = 0f;
    }

    private void LateUpdate()
    {
        if (_sweepBroken || SweepAllUiText == null || !SweepAllUiText.Value
            || FixSciNotation == null || !FixSciNotation.Value)
        {
            return;
        }
        try
        {
            float now = Time.unscaledTime;
            if (now >= _nextRescanAt)
            {
                _nextRescanAt = now + 5f;
                TextCache.Clear();
                TextCache.AddRange(FindObjectsOfType<Text>());
            }
            if (now < _nextSweepAt)
            {
                return;
            }
            _nextSweepAt = now + 0.1f; // 10 Hz is well under one frame of work

            for (int i = 0; i < TextCache.Count; i++)
            {
                Text t = TextCache[i];
                if (!t)
                {
                    continue;
                }
                string current = t.text;
                if (string.IsNullOrEmpty(current) || current.IndexOf('E') < 0)
                {
                    continue;
                }
                string fixedText = RewriteSciNotation(current);
                if (fixedText != null)
                {
                    t.text = fixedText;
                }
            }
        }
        catch (Exception ex)
        {
            _sweepBroken = true;
            Log.LogError("Readable Numbers: UI scientific-notation sweep disabled after error: " + ex);
        }
    }

    // Returns the rewritten string, or null when nothing looked like E notation (so callers can
    // skip the assignment entirely and avoid dirtying the canvas).
    internal static string RewriteSciNotation(string text)
    {
        SciSb.Length = 0;
        bool changed = false;
        int i = 0;
        int len = text.Length;
        while (i < len)
        {
            // A candidate starts at a digit (optionally preceded by '-') and must contain 'E'.
            int start = i;
            if (char.IsDigit(text[i]) || (text[i] == '-' && i + 1 < len && char.IsDigit(text[i + 1])))
            {
                int j = (text[i] == '-') ? i + 1 : i;
                while (j < len && (char.IsDigit(text[j]) || text[j] == '.')) j++;
                if (j < len && (text[j] == 'E' || text[j] == 'e'))
                {
                    int expStart = j + 1;
                    int k = expStart;
                    if (k < len && (text[k] == '+' || text[k] == '-')) k++;
                    int digits = k;
                    while (k < len && char.IsDigit(text[k])) k++;
                    if (k > digits)
                    {
                        double value;
                        if (double.TryParse(text.Substring(start, k - start), NumberStyles.Float,
                                Inv, out value))
                        {
                            SciSb.Append(FormatNamed(value));
                            changed = true;
                            i = k;
                            continue;
                        }
                    }
                }
            }
            SciSb.Append(text[start]);
            i = start + 1;
        }
        return changed ? SciSb.ToString() : null;
    }

    // Named-scale rendering shared with the damage formatter's NamedUnits mode.
    private static string FormatNamed(double value)
    {
        double abs = value < 0 ? -value : value;
        if (abs < 1e6)
        {
            return Math.Floor(value).ToString(GroupedInt, Inv);
        }
        int idx = (int)Math.Floor(Math.Log10(abs) / 3.0) - 2;
        if (idx >= ScaleNames.Length)
        {
            idx = ScaleNames.Length - 1;
        }
        if (idx < 0)
        {
            idx = 0;
        }
        double scaled = value / Math.Pow(10.0, 6 + 3 * idx);
        if (scaled < 1.0 && scaled > -1.0 && idx > 0)
        {
            idx--;
            scaled = value / Math.Pow(10.0, 6 + 3 * idx);
        }
        return scaled.ToString(GroupedOneDecimal, Inv) + ScaleNames[idx];
    }
}

// Shared contract consumed by the "Mods" side menu via reflection.
// Type name, namespace-level visibility and method signature must stay EXACTLY as-is.
//
// Single-row semantics for "numbers.mode": the row is a master switch over ALL of this
// plugin's number rewriting.
//   ON  ("Numbers: Named")   -> Mode = NamedUnits, FixScientificNotationText = true,
//                               damage prefix active.
//   OFF ("Numbers: Vanilla") -> damage prefix falls through to the game's own formatter and
//                               the UI scientific-notation sweep is switched off.
// Turning it back ON always selects NamedUnits (it does not restore GroupedSuffix /
// FullBelowBillion / FullAlways - those remain config-file-only choices, and are reported by
// the row as "Numbers: Other" so the menu never mislabels them).
// The gold-counter option (FormatMoney) is a separate, unrelated feature and is left untouched.
public static class ModMenuProvider
{
    private static string ModeDescription()
    {
        try
        {
            bool active = !Plugin.VanillaNumbersOverride;
            if (!active)
            {
                return "Our number formatting is off: damage, DPS and the HP/mana readouts show "
                    + "the game's own text, including scientific notation like 1.03E+11.";
            }
            return "Shows big numbers at their nearest named scale - 510 Billion, 1.2 Trillion, "
                + "3.4 Quadrillion - for damage, DPS and the HP/mana readouts.";
        }
        catch
        {
            return "Switches between named-scale numbers and the game's own formatting.";
        }
    }

    // Each row: new object[] { string id, Func<string> label, Func<bool> state, Action onClick }
    public static object[][] GetMenuItems()
    {
        try
        {
            return new object[][]
            {
                new object[]
                {
                    "numbers.mode",
                    (Func<string>)ModeLabel,
                    (Func<bool>)ModeState,
                    (Action)ModeClick,
                    (Func<string>)ModeDescription
                }
            };
        }
        catch
        {
            return new object[0][];
        }
    }

    private static bool IsActive()
    {
        return !Plugin.VanillaNumbersOverride;
    }

    private static string ModeLabel()
    {
        try
        {
            if (!IsActive())
            {
                return "Numbers: Vanilla";
            }
            if (Plugin.Mode != null && Plugin.Mode.Value != Plugin.DamageFormatMode.NamedUnits)
            {
                return "Numbers: Other";
            }
            return "Numbers: Named";
        }
        catch
        {
            return "Numbers: Named";
        }
    }

    private static bool ModeState()
    {
        try
        {
            return IsActive();
        }
        catch
        {
            return false;
        }
    }

    private static void ModeClick()
    {
        try
        {
            if (IsActive())
            {
                // -> vanilla: stop rewriting damage text and stop the UI sweep.
                Plugin.VanillaNumbersOverride = true;
                if (Plugin.FixSciNotation != null)
                {
                    Plugin.FixSciNotation.Value = false;
                }
            }
            else
            {
                // -> our formatting: named units everywhere, sweep back on.
                Plugin.VanillaNumbersOverride = false;
                if (Plugin.Mode != null)
                {
                    Plugin.Mode.Value = Plugin.DamageFormatMode.NamedUnits;
                }
                if (Plugin.FixSciNotation != null)
                {
                    Plugin.FixSciNotation.Value = true;
                }
            }
        }
        catch
        {
            // never propagate into the menu's draw loop
        }
    }
}
