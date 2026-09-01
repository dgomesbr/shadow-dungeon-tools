using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using FinkFramework.Runtime.Singleton;
using HarmonyLib;
using Inputs.Cursors;
using UnityEngine;
using UnityEngine.UI;
using UText = UnityEngine.UI.Text;

namespace AdvancedTooltips;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "custom.advancedtooltips";
    public const string PluginName = "Advanced Tooltips";
    public const string PluginVersion = "1.0.2";

    internal static ManualLogSource Log;
    internal static ConfigEntry<bool> ShowAffixRanges;
    internal static ConfigEntry<bool> GroundLootTooltips;

    private Harmony _harmony;

    // ---- Feature A (affix roll ranges) cached reflection ----
    private static bool _affixPatched;
    private static bool _affixBroken;
    private static MethodInfo _miFindWeaponTemplate;      // ItemManager.FindWeaponTemplate(WeaponClass) : Item_MB
    private static Func<int, bool> _isIntegerGrowthIndex; // ItemManager.IsWeaponIntegerGrowthIndex(int)
    private static Func<int, bool> _isMijingExtraIndex;   // ItemManager.IsMijingExtraIntegerIndex(int)
    private static Func<int, bool> _isFloatWholeIndex;    // ItemManager.IsWeaponFloatWholeIndex(int)
    private static Func<int, bool> _isFloatOneDecIndex;   // ItemManager.IsWeaponFloatOneDecimalIndex(int)
    private static readonly object[] _findTemplateArgs = new object[1];

    // ---- Feature B (ground-loot hover tooltips) cached reflection ----
    private static bool _hoverPatched;
    private static bool _hoverBroken;
    private static MethodInfo _miFillGemTip;              // GameUIManager.FillGemTip(BaoshiClass, Vector2)
    private static MethodInfo _miFillUseItemTip;          // GameUIManager.FillUseItemTip(UseItemClass, Vector2)
    private static MethodInfo _miLayoutSingleTip;         // GameUIManager.LayoutSingleTip(RectTransform, Vector3, bool)
    private static MethodInfo _miRefreshTipLayout;        // static GameUIManager.RefreshWeaponTipLayout(RectTransform)
    private static bool _tipAHelpersOk;
    private static readonly object[] _fillArgs = new object[2];
    private static readonly object[] _refreshArgs = new object[1];
    private static readonly object[] _layoutArgs = new object[3];
    private static DropItem _hoverDrop;
    private static bool _hoverUsedTipB;

    // Reused scratch buffers (tooltip fills are event-driven, but keep churn low anyway).
    private static readonly List<WPDT_A> _rtMain = new List<WPDT_A>(8);
    private static readonly StringBuilder _sb = new StringBuilder(512);
    private static readonly float[] _elemScratch = new float[6];

    private const string SuffixOpen = " <color=#8C8C8C>(";
    private const string SuffixClose = ")</color>";

    private void Awake()
    {
        Log = base.Logger;
        ShowAffixRanges = base.Config.Bind("Tooltips", "ShowAffixRollRanges", true,
            "Append the possible roll range \"(min~max)\" to each main-affix line of weapon tooltips " +
            "(base damage/health/mana, main-affix stats, element lines), computed from the item's " +
            "template table and the game's own generation formulas. Purely visual.");
        GroundLootTooltips = base.Config.Bind("Tooltips", "GroundLootHoverTooltips", true,
            "Hovering an item lying on the ground shows its real tooltip (weapon / gem / consumable) " +
            "without picking it up. Hidden again when the cursor leaves the item. Purely visual.");

        _harmony = new Harmony(PluginGuid);
        SetupAffixRangePatches();
        SetupHoverPatches();
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
    }

    // ================================================================
    // Feature A: affix roll ranges on weapon tooltips
    // ================================================================

    private void SetupAffixRangePatches()
    {
        try
        {
            _miFindWeaponTemplate = AccessTools.Method(typeof(ItemManager), "FindWeaponTemplate", new[] { typeof(WeaponClass) });
            _isIntegerGrowthIndex = CreateIntPredicate(typeof(ItemManager), "IsWeaponIntegerGrowthIndex");
            _isMijingExtraIndex = CreateIntPredicate(typeof(ItemManager), "IsMijingExtraIntegerIndex");
            _isFloatWholeIndex = CreateIntPredicate(typeof(ItemManager), "IsWeaponFloatWholeIndex");
            _isFloatOneDecIndex = CreateIntPredicate(typeof(ItemManager), "IsWeaponFloatOneDecimalIndex");
            MethodInfo fillA = AccessTools.Method(typeof(GameUIManager), "FillWeaponTipA", new[] { typeof(WeaponClass), typeof(Vector2) });
            MethodInfo fillB = AccessTools.Method(typeof(GameUIManager), "FillWeaponTipB", new[] { typeof(WeaponClass) });

            if (_miFindWeaponTemplate == null || _isIntegerGrowthIndex == null || _isMijingExtraIndex == null
                || _isFloatWholeIndex == null || _isFloatOneDecIndex == null || fillA == null || fillB == null)
            {
                Log.LogWarning("AdvancedTooltips: could not resolve weapon generation/tooltip methods - affix roll ranges disabled.");
                return;
            }

            _harmony.Patch(fillA, postfix: new HarmonyMethod(typeof(Plugin), nameof(FillWeaponTipAPostfix)));
            _harmony.Patch(fillB, postfix: new HarmonyMethod(typeof(Plugin), nameof(FillWeaponTipBPostfix)));
            _affixPatched = true;
            Log.LogInfo("AdvancedTooltips: affix roll ranges hooked (GameUIManager.FillWeaponTipA/FillWeaponTipB).");
        }
        catch (Exception ex)
        {
            Log.LogWarning("AdvancedTooltips: failed to set up affix roll ranges - feature disabled. " + ex.Message);
            _affixPatched = false;
        }
    }

    private static Func<int, bool> CreateIntPredicate(Type type, string name)
    {
        MethodInfo mi = AccessTools.Method(type, name, new[] { typeof(int) });
        if (mi == null || !mi.IsStatic || mi.ReturnType != typeof(bool))
        {
            return null;
        }
        return (Func<int, bool>)Delegate.CreateDelegate(typeof(Func<int, bool>), mi);
    }

    private static void FillWeaponTipAPostfix(GameUIManager __instance, WeaponClass wp)
    {
        try
        {
            AppendRollRanges(wp, __instance.WP_mainA);
        }
        catch (Exception ex)
        {
            DisableAffixFeature(ex);
        }
    }

    private static void FillWeaponTipBPostfix(GameUIManager __instance, WeaponClass wp)
    {
        try
        {
            AppendRollRanges(wp, __instance.WP_mainB);
        }
        catch (Exception ex)
        {
            DisableAffixFeature(ex);
        }
    }

    private static void DisableAffixFeature(Exception ex)
    {
        if (!_affixBroken)
        {
            _affixBroken = true;
            Log.LogError("AdvancedTooltips: error while appending affix roll ranges - feature disabled for this session.");
            Log.LogError(ex);
        }
    }

    private static void AppendRollRanges(WeaponClass wp, UText target)
    {
        if (_affixBroken || !_affixPatched || !ShowAffixRanges.Value || wp == null || !target)
        {
            return;
        }
        string text = target.text;
        if (string.IsNullOrEmpty(text) || !SingletonMonoScope<ItemManager>.HasInstance)
        {
            return;
        }
        ItemManager im = SingletonMonoScope<ItemManager>.Instance;

        _findTemplateArgs[0] = wp;
        Item_MB mb = _miFindWeaponTemplate.Invoke(im, _findTemplateArgs) as Item_MB;
        if (mb == null)
        {
            return;
        }

        // WeaponClass.GetMain() emits lines in a fixed order:
        //   [damage] [health] [mana]  (each only when Final > 0)
        //   one line per wp.Main entry that maps to a known display index
        //   [Fire] [Frozen] [Thunder] [Poison] [Physics] [Shadow]  (each only when > 0)
        string[] lines = text.Split('\n');

        bool hasDmg = wp.DamageFinal > 0f;
        bool hasHp = wp.HealthFinal > 0f;
        bool hasMp = wp.ManaFinal > 0f;
        int baseCount = (hasDmg ? 1 : 0) + (hasHp ? 1 : 0) + (hasMp ? 1 : 0);

        _elemScratch[0] = wp.Fire;
        _elemScratch[1] = wp.Frozen;
        _elemScratch[2] = wp.Thunder;
        _elemScratch[3] = wp.Poison;
        _elemScratch[4] = wp.Physics;
        _elemScratch[5] = wp.Shadow;
        int elemCount = 0;
        for (int e = 0; e < 6; e++)
        {
            if (_elemScratch[e] > 0f)
            {
                elemCount++;
            }
        }

        int mainLineCount = lines.Length - baseCount - elemCount;
        if (mainLineCount < 0)
        {
            return; // layout does not match our expectation - leave the tooltip untouched
        }

        _rtMain.Clear();
        if (wp.Main != null)
        {
            for (int i = 0; i < wp.Main.Length; i++)
            {
                WPDT_A entry = wp.Main[i];
                if (entry != null && entry.Index != 0)
                {
                    _rtMain.Add(entry);
                }
            }
        }
        // Only annotate main-affix lines when they map 1:1 onto runtime Main entries
        // (some exotic indexes render no line, which would shift the mapping).
        bool mapMain = mainLineCount == _rtMain.Count;

        StringBuilder sb = _sb;
        sb.Length = 0;
        int li = 0;

        if (hasDmg)
        {
            AppendLine(sb, lines[li++], BaseStatRange(im, mb, wp, mb.Damage, (int)wp.DamageFinal));
        }
        if (hasHp)
        {
            AppendLine(sb, lines[li++], BaseStatRange(im, mb, wp, mb.Health, (int)wp.HealthFinal));
        }
        if (hasMp)
        {
            AppendLine(sb, lines[li++], BaseStatRange(im, mb, wp, mb.Mana, (int)wp.ManaFinal));
        }
        for (int i = 0; i < mainLineCount; i++)
        {
            AppendLine(sb, lines[li++], mapMain ? MainStatRange(im, mb, wp, _rtMain[i]) : null);
        }
        for (int e = 0; e < 6; e++)
        {
            if (_elemScratch[e] > 0f)
            {
                AppendLine(sb, lines[li++], ElementRange(im, wp, mb.Element, (int)_elemScratch[e]));
            }
        }

        if (li != lines.Length)
        {
            return; // paranoia: never write a partially rebuilt tooltip
        }
        target.text = sb.ToString();
    }

    private static void AppendLine(StringBuilder sb, string line, string suffix)
    {
        if (sb.Length > 0)
        {
            sb.Append('\n');
        }
        sb.Append(line);
        if (!string.IsNullOrEmpty(suffix))
        {
            sb.Append(suffix);
        }
    }

    // Base Damage/Health/Mana roll (ItemManager.SetWPdata:2433-2435):
    //   Floor(mb.X * MultiLevelA^level * (1 +/- RandomCount) * GivePRC_Base(level)),
    // possibly multiplied by 1.1-1.5 when the template's special affix rolled away
    // (SetWPdata:2492-2573), then displayed as (int)(X * GetBaseValueMultiplier()).
    private static string BaseStatRange(ItemManager im, Item_MB mb, WeaponClass wp, float src, int actual)
    {
        if (src <= 0f || wp.ZQ_CountMax > 0)
        {
            return null; // enhanced weapons no longer reflect their drop roll
        }
        float factor = Mathf.Pow(im.MultiLevelA, wp.Level) * ItemManager.GivePRC_Base(wp.Level);
        float lo = Mathf.Floor(src * factor * (1f - im.RandomCount));
        float hi = Mathf.Floor(src * factor * (1f + im.RandomCount));
        if (!wp.HasSPC(0) && TemplateHasSpc(mb))
        {
            hi = Mathf.Floor(hi * MaxSpcCompensation(wp.CharType, wp.Quality));
        }
        float mult = wp.GetBaseValueMultiplier();
        int min = (int)(lo * mult);
        int max = (int)(hi * mult);
        if (min >= max || actual < min || actual > max)
        {
            return null;
        }
        return SuffixOpen + min + "~" + max + SuffixClose;
    }

    private static bool TemplateHasSpc(Item_MB mb)
    {
        if (mb.SPC == null)
        {
            return false;
        }
        for (int i = 0; i < mb.SPC.Count; i++)
        {
            WPSPC spc = mb.SPC[i];
            if (spc != null && spc.Index != 0)
            {
                return true;
            }
        }
        return false;
    }

    // Highest possible "no special affix" compensation multiplier (SetWPdata:2492-2573).
    private static float MaxSpcCompensation(int charType, int quality)
    {
        if (charType <= 1)
        {
            return quality < 4 ? 1.4f : 1.5f;
        }
        return quality < 4 ? 1.3f : 1.4f;
    }

    // Main-affix roll (ItemManager.GenerateWeaponStatValue:3600-3620):
    // template NB value classified per stat index, then scaled/randomized.
    private static string MainStatRange(ItemManager im, Item_MB mb, WeaponClass wp, WPDT_A entry)
    {
        float src;
        if (!TryGetTemplateNumber(mb, entry.Index, out src))
        {
            return null;
        }
        int level = wp.Level;
        int quality = wp.Quality;
        int dropScene = Mathf.Clamp(wp.DropScene, 0, 4);
        bool mijing = dropScene > 0;

        float lo;
        float hi;
        if (entry.Index >= 3 && entry.Index <= 6)
        {
            // recovery stats: src * MultiLevelA^level * (1 +/- RandomCount) * GivePRC_Base
            float factor = Mathf.Pow(im.MultiLevelA, level) * ItemManager.GivePRC_Base(level);
            lo = src * factor * (1f - im.RandomCount);
            hi = src * factor * (1f + im.RandomCount);
        }
        else if (_isIntegerGrowthIndex(entry.Index))
        {
            // ApplyWeaponIntegerGrowth:3657-3679
            lo = Mathf.Floor(src);
            int bonus = 0;
            if (mijing || level >= 80)
            {
                bonus = quality < 5 ? 1 : 2;
            }
            else if (level >= 50)
            {
                bonus = 1;
            }
            hi = lo + bonus;
        }
        else if (_isMijingExtraIndex(entry.Index))
        {
            // ApplyMijingExtraIntegerGrowth:3681-3712
            lo = Mathf.Floor(src);
            int add = 0;
            if (mijing && quality >= 5)
            {
                int n = Mathf.FloorToInt(src);
                add = n < 5 ? 1 : (n < 9 ? 2 : 3);
            }
            hi = lo + add;
        }
        else if (_isFloatWholeIndex(entry.Index) || _isFloatOneDecIndex(entry.Index))
        {
            // GetWeaponStatRandomMultiplier:3622-3655
            float bandLo;
            float bandHi;
            GetRollBand(mijing, dropScene, level, out bandLo, out bandHi);
            lo = src * bandLo;
            hi = src * bandHi;
        }
        else
        {
            return null; // stat is copied verbatim from the table - no roll range to show
        }

        if (entry.number < lo - 0.001f || entry.number > hi + 0.001f)
        {
            return null; // value no longer inside its drop range (modified item / unknown source)
        }
        string a = ItemManager.FormatWeaponStatValue(entry.Index, lo);
        string b = ItemManager.FormatWeaponStatValue(entry.Index, hi);
        if (a == b)
        {
            return null;
        }
        return SuffixOpen + a + "~" + b + SuffixClose;
    }

    private static void GetRollBand(bool mijing, int dropScene, int level, out float lo, out float hi)
    {
        if (mijing)
        {
            switch (dropScene)
            {
                case 1: lo = 1.2f; hi = 1.3f; return;
                case 2: lo = 1.2f; hi = 1.4f; return;
                case 3: lo = 1.3f; hi = 1.5f; return;
                default: lo = 1.4f; hi = 1.6f; return;
            }
        }
        if (level < 40) { lo = 0.9f; hi = 1f; return; }
        if (level < 50) { lo = 0.9f; hi = 1.1f; return; }
        if (level < 70) { lo = 1f; hi = 1.1f; return; }
        if (level < 80) { lo = 1f; hi = 1.2f; return; }
        if (level < 90) { lo = 1f; hi = 1.3f; return; }
        lo = 1.1f; hi = 1.3f;
    }

    private static bool TryGetTemplateNumber(Item_MB mb, int index, out float number)
    {
        number = 0f;
        bool found = false;
        bool ambiguous = false;
        ScanTemplateArray(mb.Main, index, ref found, ref ambiguous, ref number);
        ScanTemplateArray(mb.RateMain, index, ref found, ref ambiguous, ref number);
        return found && !ambiguous;
    }

    // Marks the lookup ambiguous when two template entries share the same stat index but
    // carry different table base values (the runtime entry could stem from either).
    private static void ScanTemplateArray(WPDT_A[] source, int index, ref bool found, ref bool ambiguous, ref float number)
    {
        if (source == null || ambiguous)
        {
            return;
        }
        for (int i = 0; i < source.Length; i++)
        {
            WPDT_A t = source[i];
            if (t == null || t.Index != index)
            {
                continue;
            }
            if (found)
            {
                if (!Mathf.Approximately(number, t.number))
                {
                    ambiguous = true;
                    return;
                }
            }
            else
            {
                number = t.number;
                found = true;
            }
        }
    }

    // Element roll (ItemManager.ApplyElement:4146-4196):
    //   split = Random(min..max by mb.Element size), per element:
    //   FloorToInt((mb.Element/split + Floor(mb.Element * GivePRC_PRC(level))) * (1 +/- RDEL))
    private static string ElementRange(ItemManager im, WeaponClass wp, float baseVal, int actual)
    {
        if (baseVal <= 0f || wp.JHEL_Count > 0)
        {
            return null; // element-enhanced weapons no longer reflect their drop roll
        }
        int splitMin;
        int splitMax;
        if (baseVal < 10f) { splitMin = 1; splitMax = 1; }
        else if (baseVal < 25f) { splitMin = 1; splitMax = 2; }
        else if (baseVal < 45f) { splitMin = 2; splitMax = 3; }
        else { splitMin = 2; splitMax = 4; }

        float prcAdd = Mathf.FloorToInt(baseVal * ItemManager.GivePRC_PRC(wp.Level));
        int min = Mathf.FloorToInt((baseVal / splitMax + prcAdd) * (1f - im.RDEL));
        int max = Mathf.FloorToInt((baseVal / splitMin + prcAdd) * (1f + im.RDEL));
        if (min >= max || actual < min || actual > max)
        {
            return null;
        }
        return SuffixOpen + min + "~" + max + SuffixClose;
    }

    // ================================================================
    // Feature B: ground-loot hover tooltips
    // ================================================================

    private void SetupHoverPatches()
    {
        try
        {
            MethodInfo onHover = AccessTools.Method(typeof(DropItem), "OnHover", new[] { typeof(bool) });
            if (onHover == null)
            {
                Log.LogWarning("AdvancedTooltips: DropItem.OnHover(bool) not found - ground-loot hover tooltips disabled.");
                return;
            }
            // Gem/consumable tips have no public Vector3-anchored entry point (ShowBSTip/ShowUseTip
            // require inventory slot grids), so replicate their bodies via the private fill+layout helpers.
            _miFillGemTip = AccessTools.Method(typeof(GameUIManager), "FillGemTip", new[] { typeof(BaoshiClass), typeof(Vector2) });
            _miFillUseItemTip = AccessTools.Method(typeof(GameUIManager), "FillUseItemTip", new[] { typeof(UseItemClass), typeof(Vector2) });
            _miLayoutSingleTip = AccessTools.Method(typeof(GameUIManager), "LayoutSingleTip", new[] { typeof(RectTransform), typeof(Vector3), typeof(bool) });
            _miRefreshTipLayout = AccessTools.Method(typeof(GameUIManager), "RefreshWeaponTipLayout", new[] { typeof(RectTransform) });
            _tipAHelpersOk = _miFillGemTip != null && _miFillUseItemTip != null
                && _miLayoutSingleTip != null && _miRefreshTipLayout != null;
            if (!_tipAHelpersOk)
            {
                Log.LogWarning("AdvancedTooltips: gem/consumable tip helpers not found - hover tooltips limited to weapons.");
            }

            _harmony.Patch(onHover, postfix: new HarmonyMethod(typeof(Plugin), nameof(DropItemOnHoverPostfix)));
            _hoverPatched = true;
            Log.LogInfo("AdvancedTooltips: ground-loot hover tooltips hooked (DropItem.OnHover).");
        }
        catch (Exception ex)
        {
            Log.LogWarning("AdvancedTooltips: failed to set up hover tooltips - feature disabled. " + ex.Message);
            _hoverPatched = false;
        }
    }

    private static void DropItemOnHoverPostfix(DropItem __instance, bool isHovering)
    {
        try
        {
            if (!_hoverPatched)
            {
                return;
            }
            // _hoverBroken and the config toggle must NOT gate the hide branch below:
            // flipping the config (or an exception) while a tooltip is visible would
            // otherwise leave it stuck on screen with no hover-exit able to close it.
            if (!SingletonMonoScope<GameUIManager>.HasInstance)
            {
                _hoverDrop = null;
                return;
            }
            GameUIManager gui = SingletonMonoScope<GameUIManager>.Instance;

            if (!isHovering)
            {
                if (_hoverDrop == __instance)
                {
                    _hoverDrop = null;
                    if (_hoverUsedTipB)
                    {
                        gui.HideTooltipB();
                    }
                    else
                    {
                        gui.HideTooltipA();
                    }
                }
                return;
            }

            if (_hoverBroken || !GroundLootTooltips.Value)
            {
                return; // show path only - hide path above always runs
            }
            DropItemController parent = __instance.parent;
            if (!parent || !parent.LuoDi)
            {
                return; // still airborne / no payload holder
            }
            // Skip while any modal or inventory-style panel is open - those screens drive
            // the same shared tooltip canvases themselves.
            if (gui.IsInModalState || gui.Opened_IV || gui.Opened_shop || gui.Opened_warehouse
                || gui.Opened_Character || gui.Opened_Talent || gui.Opened_weapon || gui.Opened_baoshi)
            {
                return;
            }
            Camera cam = Camera.main;
            if (!cam)
            {
                return;
            }
            Vector3 screenPos = cam.WorldToScreenPoint(parent.transform.position);

            switch (parent.ItemType)
            {
                case 0:
                {
                    WeaponClass wpn = parent.weapon;
                    if (wpn == null || string.IsNullOrEmpty(wpn.ItemName))
                    {
                        return; // empty payload (e.g. pooled/reset drop)
                    }
                    // ShowWPTipB dereferences these singletons internally.
                    if (!SingletonMonoScope<CursorInputManager>.HasInstance || !SingletonMonoScope<TalentManager>.HasInstance)
                    {
                        return;
                    }
                    gui.ShowWPTipB(screenPos, wpn);
                    _hoverDrop = __instance;
                    _hoverUsedTipB = true;
                    break;
                }
                case 1:
                {
                    BaoshiClass bs = parent.baoshi;
                    if (bs == null || string.IsNullOrEmpty(bs.ItemName) || !_tipAHelpersOk)
                    {
                        return;
                    }
                    ShowTipAViaFill(gui, _miFillGemTip, bs, screenPos);
                    _hoverDrop = __instance;
                    _hoverUsedTipB = false;
                    break;
                }
                case 2:
                {
                    UseItemClass use = parent.useitem;
                    if (use == null || string.IsNullOrEmpty(use.ItemName) || !_tipAHelpersOk)
                    {
                        return;
                    }
                    ShowTipAViaFill(gui, _miFillUseItemTip, use, screenPos);
                    _hoverDrop = __instance;
                    _hoverUsedTipB = false;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            if (!_hoverBroken)
            {
                _hoverBroken = true;
                Log.LogError("AdvancedTooltips: error in ground-loot hover handler - feature disabled for this session.");
                Log.LogError(ex);
            }
        }
    }

    // Mirrors GameUIManager.ShowBSTip/ShowUseTip (:2479/:2499) but anchored to an arbitrary
    // screen position instead of an inventory slot grid.
    private static void ShowTipAViaFill(GameUIManager gui, MethodInfo fill, object payload, Vector3 screenPos)
    {
        gui.HideAllWeaponTips();
        Vector2 pos = new Vector2(screenPos.x / Screen.width, screenPos.y / Screen.height);
        bool preferRightSide = pos.x < 0.5f;

        _fillArgs[0] = payload;
        _fillArgs[1] = pos;
        fill.Invoke(gui, _fillArgs);

        _refreshArgs[0] = gui.WP_RectA;
        _miRefreshTipLayout.Invoke(null, _refreshArgs);

        _layoutArgs[0] = gui.WP_RectA;
        _layoutArgs[1] = screenPos;
        _layoutArgs[2] = preferRightSide;
        _miLayoutSingleTip.Invoke(gui, _layoutArgs);

        gui.WeaponCavA.alpha = 1f;
    }

    // ---- Mod menu integration -------------------------------------------------------------
    // Exposed so the shared "Mods" side menu can read/flip these features without a hotkey.
    internal static bool AffixFeatureLive
    {
        get { return _affixPatched && !_affixBroken; }
    }

    internal static bool HoverFeatureLive
    {
        get { return _hoverPatched && !_hoverBroken; }
    }
}

// Shared contract consumed by the "Mods" side menu via reflection.
// Type name, namespace-level visibility and method signature must stay EXACTLY as-is.
public static class ModMenuProvider
{
    // Each row: new object[] { string id, Func<string> label, Func<bool> state, Action onClick,
    //                          Func<string> description }
    // The 5th element (hover tooltip text) is optional in the contract and, like label() and
    // state(), may be evaluated every frame the menu is open - so it must never throw either.
    public static object[][] GetMenuItems()
    {
        try
        {
            return new object[][]
            {
                new object[]
                {
                    "tooltips.rollranges",
                    (Func<string>)RollRangesLabel,
                    (Func<bool>)RollRangesState,
                    (Action)RollRangesClick,
                    (Func<string>)RollRangesDescription
                },
                new object[]
                {
                    "tooltips.groundloot",
                    (Func<string>)GroundLootLabel,
                    (Func<bool>)GroundLootState,
                    (Action)GroundLootClick,
                    (Func<string>)GroundLootDescription
                }
            };
        }
        catch
        {
            return new object[0][];
        }
    }

    // Mirrors the "n/a" label variants: when the patches never installed (or a runtime error
    // retired the feature) the tooltip has to say so, otherwise a dead row looks merely "off".
    private static string RollRangesDescription()
    {
        try
        {
            if (!Plugin.AffixFeatureLive)
            {
                return "Roll ranges are unavailable: the game's tooltip or item-generation methods could not be found, so tooltips stay vanilla.";
            }
            return "Adds each rollable affix's possible minimum and maximum to item tooltips so you can judge a drop at a glance. Applies to the next tooltip you open.";
        }
        catch
        {
            return "Adds the possible minimum and maximum to each rollable affix line of an item tooltip.";
        }
    }

    private static string GroundLootDescription()
    {
        try
        {
            if (!Plugin.HoverFeatureLive)
            {
                return "Ground-loot tooltips are unavailable: the game's hover method was not found, or the feature switched itself off after an error.";
            }
            return "Hovering an item lying on the ground shows its full tooltip without picking it up; the tooltip hides again when the cursor leaves.";
        }
        catch
        {
            return "Hovering an item on the ground shows its full tooltip without picking it up.";
        }
    }

    private static string RollRangesLabel()
    {
        try
        {
            return Plugin.AffixFeatureLive ? "Roll Ranges" : "Roll Ranges n/a";
        }
        catch
        {
            return "Roll Ranges";
        }
    }

    private static bool RollRangesState()
    {
        try
        {
            return Plugin.AffixFeatureLive && Plugin.ShowAffixRanges != null && Plugin.ShowAffixRanges.Value;
        }
        catch
        {
            return false;
        }
    }

    private static void RollRangesClick()
    {
        try
        {
            if (Plugin.ShowAffixRanges != null)
            {
                Plugin.ShowAffixRanges.Value = !Plugin.ShowAffixRanges.Value;
            }
        }
        catch
        {
            // never propagate into the menu's draw loop
        }
    }

    private static string GroundLootLabel()
    {
        try
        {
            return Plugin.HoverFeatureLive ? "Loot Hover Tips" : "Loot Tips n/a";
        }
        catch
        {
            return "Loot Hover Tips";
        }
    }

    private static bool GroundLootState()
    {
        try
        {
            return Plugin.HoverFeatureLive && Plugin.GroundLootTooltips != null && Plugin.GroundLootTooltips.Value;
        }
        catch
        {
            return false;
        }
    }

    private static void GroundLootClick()
    {
        try
        {
            if (Plugin.GroundLootTooltips != null)
            {
                Plugin.GroundLootTooltips.Value = !Plugin.GroundLootTooltips.Value;
            }
        }
        catch
        {
            // never propagate into the menu's draw loop
        }
    }
}
