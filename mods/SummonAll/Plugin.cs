using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Entity.Comp.CompanionAI;
using FinkFramework.Runtime.Singleton;
using HarmonyLib;
using UnityEngine;

namespace SummonAll;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("max.characterutilities", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "custom.summonall";
    public const string PluginName = "Summon All";
    public const string PluginVersion = "1.3.0";

    internal static ManualLogSource Log;
    internal static ConfigEntry<bool> FairMode;
    internal static ConfigEntry<bool> ToggleMode;
    // The summon-row relocation is always on; this latch only disables it after an error.
    private static bool _relocateBroken;
    internal static ConfigEntry<float> SummonBarOffset;

    private static string _status = "";

    // Alive-count cache so the IMGUI label doesn't rescan on every GUI event.
    private static int _aliveCache;
    private static int _aliveCacheFrame = -1;

    private Harmony _harmony;
    private bool _embedded;

    // Relocation is idempotent and re-checked every LateUpdate; this only de-duplicates logging.
    private string _lastRelocateLog;
    private int _lastIconCount = -1;

    private void Awake()
    {
        Log = base.Logger;
        FairMode = base.Config.Bind("Summoning", "RespectCooldownAndMana", false,
            "When true, Summon All casts each summon skill through the game's normal skill pipeline (one companion per skill, costs mana, starts the cooldown). When false, it instantly refills every summon skill to its maximum companion count for free, like the game's own after-death auto-resummon.");
        ToggleMode = base.Config.Bind("Summoning", "ToggleSummonDismiss", true,
            "When true, the Mods-menu row and the F6-window button TOGGLE: if any summons are alive they are all dismissed (handy before going back to town); otherwise everything is summoned. When false, they always summon.");
        SummonBarOffset = base.Config.Bind("UI", "SummonBarOffsetPixels", 10f,
            "Vertical gap in pixels between the skill bar and the relocated summon icon row.");

        _harmony = new Harmony(PluginGuid);
        MethodInfoPatchTarget();
        Log.LogInfo(_embedded
            ? "Summon All button embedded into the Character Utilities (F6) window."
            : "Character Utilities plugin not found - use the 'Mods' menu on the right screen edge.");
    }

    private void MethodInfoPatchTarget()
    {
        try
        {
            Type cuPluginType = AccessTools.TypeByName("CharacterUtilities.Plugin");
            System.Reflection.MethodInfo drawWindow = cuPluginType != null
                ? AccessTools.Method(cuPluginType, "DrawWindow")
                : null;
            if (drawWindow != null)
            {
                // Prefix, not Postfix: DrawWindow ends with GUI.DragWindow(), which consumes
                // clicks on any control laid out after it. Drawing first keeps the button live.
                _harmony.Patch(drawWindow, new HarmonyMethod(typeof(Plugin), nameof(DrawWindowPrefix)));
                _embedded = true;
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning("Could not embed into Character Utilities window; the 'Mods' menu row still works: " + ex.Message);
            _embedded = false;
        }
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
    }

    // No Update(): this plugin owns no keyboard shortcut any more. Summon All / Dismiss All is
    // reachable from the shared "Mods" menu (right screen edge) and from the button embedded in
    // the third-party Character Utilities F6 window.

    // LateUpdate: after the game's UI has laid out for this frame, so a re-layout cannot leave
    // the panel parked in its original spot.
    private void LateUpdate()
    {
        if (!_relocateBroken)
        {
            TryMoveSummonBar();
        }
    }

    // No OnGUI(): the old fallback IMGUI window is gone. The shared "Mods" menu owns our UI now,
    // so this plugin draws nothing of its own outside the Character Utilities prefix below.

    private static void DrawWindowPrefix()
    {
        DrawSummonSection();
        GUILayout.Space(6f);
    }

    private static void DrawSummonSection()
    {
        int alive = ToggleMode.Value ? CountAliveSummons() : 0;
        string label = alive > 0 ? "Dismiss All (" + alive + ")" : "Summon All";
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(label, GUILayout.Width(130f)))
        {
            ToggleSummons();
        }
        GUILayout.Label(string.IsNullOrEmpty(_status)
            ? (alive > 0 ? "Dismisses every active companion (nice before town)." : "Summons every companion skill you have learned.")
            : _status);
        GUILayout.EndHorizontal();
    }

    // internal, not private: ModMenuProvider (same assembly, sibling type) invokes it.
    internal static void ToggleSummons()
    {
        if (ToggleMode.Value && CountAliveSummons() > 0)
        {
            DismissAll();
        }
        else
        {
            SummonAll();
        }
    }

    // internal, not private: ModMenuProvider reads it for the row label/state.
    internal static int CountAliveSummons()
    {
        if (Time.frameCount == _aliveCacheFrame)
        {
            return _aliveCache;
        }
        _aliveCacheFrame = Time.frameCount;
        _aliveCache = 0;
        if (!SingletonMonoScope<ACTbar>.HasInstance)
        {
            return 0;
        }
        ACTbar bar = SingletonMonoScope<ACTbar>.Instance;
        if (!bar || bar.actListSkill == null)
        {
            return 0;
        }
        foreach (ACTListSkillBT skill in bar.actListSkill)
        {
            if (skill && skill.DT != null && skill.DT.type == 1 && skill.DT.comp != null)
            {
                _aliveCache += GetAliveCompCount(skill);
            }
        }
        return _aliveCache;
    }

    // Mirrors the removal pattern of the game's own ACTbar.ValidateCompCount:
    // delist from cpList, then SystemDelete (plays the death FX, cleans permanent skills).
    private static void DismissAll()
    {
        try
        {
            if (!SingletonMonoScope<ACTbar>.HasInstance)
            {
                _status = "Not in a level.";
                return;
            }
            ACTbar bar = SingletonMonoScope<ACTbar>.Instance;
            if (!bar || bar.actListSkill == null)
            {
                _status = "Not in a level.";
                return;
            }
            int dismissed = 0;
            foreach (ACTListSkillBT skill in bar.actListSkill)
            {
                if (!skill || skill.DT == null || skill.DT.type != 1 || skill.DT.comp == null || skill.cpList == null)
                {
                    continue;
                }
                for (int i = skill.cpList.Count - 1; i >= 0; i--)
                {
                    Companion comp = skill.cpList[i];
                    skill.cpList.RemoveAt(i);
                    if (comp && !comp.IsDead)
                    {
                        comp.SystemDelete();
                        dismissed++;
                    }
                }
            }
            _aliveCacheFrame = -1; // force recount
            if (SingletonMonoScope<CompanionManager>.HasInstance)
            {
                SingletonMonoScope<CompanionManager>.Instance.RequestRefreshNextFrame();
            }
            _status = dismissed > 0 ? "Dismissed " + dismissed + " summons." : "No summons to dismiss.";
            Log.LogInfo("SummonAll: " + _status);
        }
        catch (Exception ex)
        {
            _status = "ERROR: " + ex.GetBaseException().Message;
            Log.LogError(ex);
        }
    }

    private static void SummonAll()
    {
        try
        {
            if (!SingletonMonoScope<ACTbar>.HasInstance || !SingletonMonoScope<Gun>.HasInstance
                || !SingletonMonoScope<PlayerManager>.HasInstance)
            {
                _status = "Not in a level - enter a dungeon first.";
                return;
            }
            ACTbar bar = SingletonMonoScope<ACTbar>.Instance;
            Gun gun = SingletonMonoScope<Gun>.Instance;
            PlayerManager player = SingletonMonoScope<PlayerManager>.Instance;
            if (!bar || !gun || !player || !player.IsAlive || bar.actListSkill == null)
            {
                _status = "Player is not available.";
                return;
            }

            int total = 0;
            List<string> parts = new List<string>();
            List<string> rejected = new List<string>();
            foreach (ACTListSkillBT skill in bar.actListSkill)
            {
                // The game's own summon discriminator (ACTbar.TryAutoUseSkills / RestoreRebornAutoSummons).
                // Never key off ACTListSkillBT.SkillType: weapon-granted summon fathers can carry SkillType 3.
                if (!skill || skill.DT == null || skill.DT.type != 1 || skill.DT.comp == null)
                {
                    continue;
                }

                int deficit = GetMaxSummonCount(skill) - GetAliveCompCount(skill);
                if (deficit <= 0)
                {
                    continue;
                }

                if (FairMode.Value)
                {
                    if (bar.TryReleaseSkillDirect(skill, useCooldown: true, spendMana: true, skipAnimation: true))
                    {
                        total++;
                        parts.Add(skill.IndexName);
                    }
                    else
                    {
                        rejected.Add(skill.IndexName + (skill.IsCD ? " (on cooldown)" : " (not enough mana)"));
                    }
                    continue;
                }

                int spawned = 0;
                for (int i = 0; i < deficit; i++)
                {
                    Vector2 jitter = UnityEngine.Random.insideUnitCircle * 0.8f;
                    Vector3 pos = player.transform.position + new Vector3(jitter.x, jitter.y, 0f);
                    if (!gun.SpawnCompanionInstant(skill, pos))
                    {
                        break;
                    }
                    spawned++;
                }
                if (spawned > 0)
                {
                    total += spawned;
                    parts.Add($"{spawned}x {skill.IndexName}");
                }
            }

            _aliveCacheFrame = -1; // force recount
            if (SingletonMonoScope<CompanionManager>.HasInstance)
            {
                SingletonMonoScope<CompanionManager>.Instance.RequestRefreshNextFrame();
            }

            if (total > 0)
            {
                _status = $"Summoned {total}: {string.Join(", ", parts)}";
                if (rejected.Count > 0)
                {
                    _status += $" | Skipped: {string.Join(", ", rejected)}";
                }
            }
            else if (rejected.Count > 0)
            {
                _status = "Nothing summoned - " + string.Join(", ", rejected);
            }
            else
            {
                _status = "All summons already active (or nothing to summon).";
            }
            Log.LogInfo("SummonAll: " + _status);
        }
        catch (Exception ex)
        {
            _status = "ERROR: " + ex.GetBaseException().Message;
            Log.LogError(ex);
        }
    }

    // Relocates the game's summon icon row (GameUIManager UICanvas/CompList, normally pinned to
    // the top of the screen) so its bottom edge sits just above the skill bar.
    //
    // Screen space, not world space: CompList lives under GameUIManager's UICanvas while the
    // skill bar's ActionBar lives under ACTbar's own canvas. Those canvases can differ in render
    // mode and scale factor, so a world-space delta between them lands the panel in the wrong
    // place (typically off-screen). Screen pixels are the one space both agree on.
    //
    // Re-applied from LateUpdate whenever the panel drifts from its target, so a canvas layout
    // pass or a CompanionManager UI rebuild cannot quietly undo it.
    private void TryMoveSummonBar()
    {
        try
        {
            if (!SingletonMonoScope<ACTbar>.HasInstance || !SingletonMonoScope<GameUIManager>.HasInstance)
            {
                return;
            }
            ACTbar bar = SingletonMonoScope<ACTbar>.Instance;
            if (!bar)
            {
                return;
            }

            GameUIManager gui = SingletonMonoScope<GameUIManager>.Instance;
            GameObject compList = gui.compListUI;
            if (!compList)
            {
                Transform found = gui.transform.Find("UICanvas/CompList");
                compList = found ? found.gameObject : null;
            }
            Transform actionBar = bar.transform.Find("ActionBar");
            RectTransform compRect = compList ? compList.transform as RectTransform : null;
            RectTransform barRect = actionBar as RectTransform;
            RectTransform parentRect = compRect ? compRect.parent as RectTransform : null;
            if (!compRect || !barRect || !parentRect)
            {
                LogRelocateOnce("summon bar relocation waiting on UI: compList=" + (compRect ? "ok" : "missing")
                    + " actionBar=" + (barRect ? "ok" : "missing") + " parent=" + (parentRect ? "ok" : "missing"));
                return;
            }

            Camera barCam = CanvasCamera(barRect);
            Camera compCam = CanvasCamera(compRect);
            DumpLayoutOnce(compRect, barRect);

            // Screen-space anchor points: top-center of the skill bar, and the bottom-center of
            // the ICON ROW. The container's own rect is 100x0 and parked off the top of the
            // screen, so it says nothing about where the icons actually are - measure the
            // children's combined bounds instead.
            Vector2 barTop = ScreenPoint(barRect, barCam, 0.5f, 1f);
            Vector2 iconMin;
            Vector2 iconMax;
            int iconCount;
            if (!ChildrenScreenBounds(compRect, compCam, out iconMin, out iconMax, out iconCount))
            {
                _lastIconCount = 0;
                LogRelocateOnce("summon bar relocation idle: no visible summon icons yet.");
                return;
            }
            // Wait for the row to settle before centring on it: CompanionManager tears down and
            // rebuilds the icons, and a half-built row measures far narrower than the real one.
            if (iconCount != _lastIconCount)
            {
                _lastIconCount = iconCount;
                return;
            }
            Vector2 compBottom = new Vector2((iconMin.x + iconMax.x) * 0.5f, iconMin.y);

            float scale = compRect.GetComponentInParent<Canvas>() is Canvas c && c
                ? c.rootCanvas.scaleFactor
                : 1f;
            Vector2 desiredBottom = new Vector2(barTop.x, barTop.y + SummonBarOffset.Value * scale);
            Vector2 deltaScreen = desiredBottom - compBottom;
            if (deltaScreen.sqrMagnitude < 0.25f)
            {
                return; // already within half a pixel of the target
            }

            // Translate the screen delta into the panel's own parent space so arbitrary
            // anchors/pivots are respected, then nudge anchoredPosition by that amount.
            Vector2 pivotScreen = RectTransformUtility.WorldToScreenPoint(compCam, compRect.position);
            Vector2 fromLocal;
            Vector2 toLocal;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parentRect, pivotScreen, compCam, out fromLocal)
                || !RectTransformUtility.ScreenPointToLocalPointInRectangle(parentRect, pivotScreen + deltaScreen, compCam, out toLocal))
            {
                LogRelocateOnce("summon bar relocation could not map screen space into the panel's parent rect.");
                return;
            }
            compRect.anchoredPosition += toLocal - fromLocal;
            LogRelocateOnce("summon bar relocated above the skill bar: icons " + iconMin + ".." + iconMax
                + " -> bottom-center " + desiredBottom + " (delta " + deltaScreen + ").");
        }
        catch (Exception ex)
        {
            // Fail soft: disable the feature for this session rather than throwing per frame.
            _relocateBroken = true;
            Log.LogWarning("Summon bar relocation disabled after error: " + ex.Message);
        }
    }

    // One-time layout dump. If the relocation still lands wrong on someone's setup, this single
    // log line identifies the cause (different canvases/scales, a driving layout group, etc.)
    // without another guess-and-rebuild cycle.
    private bool _dumped;

    private void DumpLayoutOnce(RectTransform compRect, RectTransform barRect)
    {
        if (_dumped)
        {
            return;
        }
        _dumped = true;
        try
        {
            Log.LogInfo("Summon bar layout: comp=" + Describe(compRect) + " | bar=" + Describe(barRect)
                + " | compParentLayout=" + LayoutDriver(compRect.parent as RectTransform)
                + " | screen=" + Screen.width + "x" + Screen.height);
        }
        catch (Exception ex)
        {
            Log.LogWarning("Summon bar layout dump failed: " + ex.Message);
        }
    }

    private static string Describe(RectTransform rect)
    {
        Canvas canvas = rect.GetComponentInParent<Canvas>();
        Canvas root = canvas ? canvas.rootCanvas : null;
        return rect.name + " path=" + Path(rect.transform)
            + " rect=" + rect.rect.size + " anchored=" + rect.anchoredPosition
            + " canvas=" + (root ? root.name + "/" + root.renderMode + "/scale" + root.scaleFactor : "none");
    }

    private static string LayoutDriver(RectTransform parent)
    {
        if (!parent)
        {
            return "none";
        }
        Component[] comps = parent.GetComponents<Component>();
        string found = "";
        for (int i = 0; i < comps.Length; i++)
        {
            Component c = comps[i];
            if (!c)
            {
                continue;
            }
            string n = c.GetType().Name;
            // Layout groups and content-size fitters overwrite child positions every layout pass.
            if (n.Contains("LayoutGroup") || n.Contains("Fitter") || n.Contains("Layout"))
            {
                found += (found.Length > 0 ? "," : "") + n;
            }
        }
        return found.Length > 0 ? found : "none";
    }

    private static string Path(Transform t)
    {
        string path = t.name;
        Transform p = t.parent;
        int guard = 0;
        while (p && guard++ < 6)
        {
            path = p.name + "/" + path;
            p = p.parent;
        }
        return path;
    }

    private static Camera CanvasCamera(RectTransform rect)
    {
        Canvas canvas = rect.GetComponentInParent<Canvas>();
        if (!canvas)
        {
            return null;
        }
        Canvas root = canvas.rootCanvas;
        // Overlay canvases are camera-independent; RectTransformUtility expects null for them.
        return root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
    }

    // Combined screen-space bounds of a container's visible children. Reused scratch array keeps
    // this allocation-free after the first call.
    private static readonly Vector3[] CornerScratch = new Vector3[4];

    private static bool ChildrenScreenBounds(RectTransform parent, Camera cam,
        out Vector2 min, out Vector2 max, out int visibleCount)
    {
        min = new Vector2(float.MaxValue, float.MaxValue);
        max = new Vector2(float.MinValue, float.MinValue);
        visibleCount = 0;
        bool any = false;
        int count = parent.childCount;
        for (int i = 0; i < count; i++)
        {
            RectTransform child = parent.GetChild(i) as RectTransform;
            if (!child || !child.gameObject.activeInHierarchy)
            {
                continue;
            }
            child.GetWorldCorners(CornerScratch);
            for (int c = 0; c < 4; c++)
            {
                Vector2 sp = RectTransformUtility.WorldToScreenPoint(cam, CornerScratch[c]);
                if (sp.x < min.x) min.x = sp.x;
                if (sp.y < min.y) min.y = sp.y;
                if (sp.x > max.x) max.x = sp.x;
                if (sp.y > max.y) max.y = sp.y;
            }
            any = true;
            visibleCount++;
        }
        return any;
    }

    // Screen position of a normalized point inside a rect (0,0 = bottom-left, 1,1 = top-right).
    private static Vector2 ScreenPoint(RectTransform rect, Camera cam, float nx, float ny)
    {
        Rect r = rect.rect;
        Vector3 world = rect.TransformPoint(new Vector3(r.x + r.width * nx, r.y + r.height * ny, 0f));
        return RectTransformUtility.WorldToScreenPoint(cam, world);
    }

    private void LogRelocateOnce(string message)
    {
        if (_lastRelocateLog == message)
        {
            return;
        }
        _lastRelocateLog = message;
        Log.LogInfo(message);
    }

    // Mirrors private ACTbar.GetCurrentCompSummonCount: table Summon_count overridden by the
    // live talent-tree value (Summon_count_Last) when TalentManager has the skill.
    private static int GetMaxSummonCount(ACTListSkillBT skill)
    {
        int count = skill.DT.comp.Summon_count;
        if (!SingletonMonoScope<TalentManager>.HasInstance || !SingletonMonoScope<PlayerManager>.HasInstance
            || string.IsNullOrEmpty(skill.IndexName))
        {
            return count;
        }
        SkillXiData[] xiData = SingletonMonoScope<TalentManager>.Instance.XiData;
        if (xiData == null || skill.Xi < 0 || skill.Xi >= xiData.Length)
        {
            return count;
        }
        SkillXiData xi = xiData[skill.Xi];
        if (xi?.Comp_F != null && xi.Comp_F.TryGetValue(skill.IndexName, out SkillData_Comp_Father father) && father != null)
        {
            count = father.Summon_count_Last;
        }
        return count;
    }

    // Mirrors private static ACTbar.GetAliveCompCount + IsActiveCompanion.
    private static int GetAliveCompCount(ACTListSkillBT skill)
    {
        if (skill.cpList == null)
        {
            return 0;
        }
        int alive = 0;
        foreach (Companion comp in skill.cpList)
        {
            if (comp && comp.IsAlive && comp.gameObject.activeInHierarchy)
            {
                alive++;
            }
        }
        return alive;
    }
}

// Rows contributed to the shared "Mods" menu (docked to the right screen border). The menu finds
// this type by reflection - the name, namespace, accessibility and GetMenuItems() signature are a
// fixed contract, so do not rename any of them.
//
// Contract: every delegate must be total. A throw here would surface as a broken menu (or a
// menu-wide exception storm, since the menu calls label()/state() every frame), so every body is
// wrapped and returns a safe fallback.
public static class ModMenuProvider
{
    // Each row: new object[] { string id, Func<string> label, Func<bool> state, Action onClick }
    public static object[][] GetMenuItems()
    {
        try
        {
            return new[]
            {
                new object[]
                {
                    "summonall.toggle",
                    (Func<string>)ToggleLabel,
                    // state() is NOT null even though this row's primary use is an action.
                    // Rationale: the row is a genuine two-state toggle when ToggleSummonDismiss is
                    // on - lit means "summons are out, clicking dismisses them", unlit means
                    // "nothing summoned, clicking summons". That is exactly the summon/unsummon
                    // state the menu is asked to surface, and it is read from the same alive count
                    // the label uses, so the lit pip and the text can never disagree.
                    (Func<bool>)ToggleState,
                    (Action)ToggleClick,
                    (Func<string>)ToggleDescription
                }
            };
        }
        catch
        {
            return new object[0][];
        }
    }

    // Mirrors DrawSummonSection: when ToggleSummonDismiss is off the button always summons, so the
    // alive count is deliberately not consulted and the row never advertises a dismiss.
    private static int AliveForRow()
    {
        return Plugin.ToggleMode != null && Plugin.ToggleMode.Value ? Plugin.CountAliveSummons() : 0;
    }

    private static string ToggleDescription()
    {
        try
        {
            if (AliveForRow() > 0)
            {
                return "Dismisses every active companion at once - handy before walking back to "
                    + "town with a screen full of minions.";
            }
            return Plugin.FairMode != null && Plugin.FairMode.Value
                ? "Casts each summon skill you have learned through the normal pipeline, paying "
                  + "mana and starting cooldowns."
                : "Instantly refills every summon skill you have learned to its full companion "
                  + "count, free of mana and cooldowns.";
        }
        catch
        {
            return "Summons or dismisses all of your companions.";
        }
    }

    private static string ToggleLabel()
    {
        try
        {
            int alive = AliveForRow();
            return alive > 0 ? "Dismiss All (" + alive + ")" : "Summon All";
        }
        catch
        {
            return "Summon All";
        }
    }

    private static bool ToggleState()
    {
        try
        {
            return AliveForRow() > 0;
        }
        catch
        {
            return false;
        }
    }

    // Plugin.ToggleSummons already guards DismissAll/SummonAll internally; this wrapper covers the
    // count lookup that sits outside those try blocks so onClick() is total.
    private static void ToggleClick()
    {
        try
        {
            if (Plugin.ToggleMode == null)
            {
                return; // clicked before our Awake bound the config
            }
            Plugin.ToggleSummons();
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogError("Mods menu: Summon All row failed: " + ex);
        }
    }


}
