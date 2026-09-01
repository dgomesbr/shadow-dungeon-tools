using System;
using System.Globalization;
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
    public const string PluginVersion = "1.1.0";

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

    // Cached invariant culture: group separator is ','. Format strings are consts, so the
    // per-call cost is one unavoidable result-string allocation - same as the vanilla method.
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string GroupedInt = "#,##0";
    private const string GroupedOneDecimal = "#,##0.#";

    // Fail-soft latches: after the first runtime error we log once and permanently fall back
    // to the game's own formatting instead of throwing on every combat-text spawn.
    private static bool _damageFormatBroken;
    private static bool _moneyFormatBroken;

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

        FormatMoney = base.Config.Bind("Money", "FormatMoney", true,
            "Also apply thousands separators to the gold counter in the inventory UI " +
            "(the game shows raw digits like 184039201). Shop/forge price labels are NOT " +
            "touched - see README for why.");

        _harmony = new Harmony(PluginGuid);
        PatchDamageFormatter();
        PatchMoneyText();
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
        if (_damageFormatBroken)
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
}
