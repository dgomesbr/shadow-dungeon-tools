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
    public const string PluginVersion = "1.0.0";

    internal static ManualLogSource Log;
    internal static ConfigEntry<bool> FairMode;
    internal static ConfigEntry<KeyboardShortcut> Hotkey;

    private static string _status = "";

    private Harmony _harmony;
    private bool _embedded;
    private bool _showFallback;
    private Rect _fallbackRect = new Rect(30f, 30f, 340f, 110f);

    private void Awake()
    {
        Log = base.Logger;
        FairMode = base.Config.Bind("Summoning", "RespectCooldownAndMana", false,
            "When true, Summon All casts each summon skill through the game's normal skill pipeline (one companion per skill, costs mana, starts the cooldown). When false, it instantly refills every summon skill to its maximum companion count for free, like the game's own after-death auto-resummon.");
        Hotkey = base.Config.Bind("Summoning", "SummonAllHotkey", KeyboardShortcut.Empty,
            "Optional keyboard shortcut that triggers Summon All without opening the F6 window (e.g. F7).");

        _harmony = new Harmony(PluginGuid);
        MethodInfoPatchTarget();
        Log.LogInfo(_embedded
            ? "Summon All button embedded into the Character Utilities (F6) window."
            : "Character Utilities plugin not found - Summon All uses its own F6 window.");
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
            Log.LogWarning("Could not embed into Character Utilities window, falling back to own window: " + ex.Message);
            _embedded = false;
        }
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F6))
        {
            _status = "";
            if (!_embedded)
            {
                _showFallback = !_showFallback;
            }
        }
        if (Hotkey.Value.IsDown())
        {
            SummonAll();
        }
    }

    private void OnGUI()
    {
        if (!_embedded && _showFallback)
        {
            _fallbackRect = GUI.Window(49277, _fallbackRect, DrawFallbackWindow, "Summon All");
            _fallbackRect.x = Mathf.Clamp(_fallbackRect.x, 0f, Mathf.Max(0f, Screen.width - _fallbackRect.width));
            _fallbackRect.y = Mathf.Clamp(_fallbackRect.y, 0f, Mathf.Max(0f, Screen.height - _fallbackRect.height));
        }
    }

    private void DrawFallbackWindow(int id)
    {
        GUILayout.BeginVertical();
        GUILayout.Label("F6 toggles this window.");
        DrawSummonSection();
        GUILayout.EndVertical();
        GUI.DragWindow();
    }

    private static void DrawWindowPrefix()
    {
        DrawSummonSection();
        GUILayout.Space(6f);
    }

    private static void DrawSummonSection()
    {
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Summon All", GUILayout.Width(130f)))
        {
            SummonAll();
        }
        GUILayout.Label(string.IsNullOrEmpty(_status) ? "Summons every companion skill you have learned." : _status);
        GUILayout.EndHorizontal();
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

    // Mirrors private ACTbar.GetCurrentCompSummonCount: table Summon_count overridden by the
    // live talent-tree value (Summon_count_Last) when TalentManager has the skill.
    private static int GetMaxSummonCount(ACTListSkillBT skill)
    {
        int count = skill.DT.comp.Summon_count;
        if (!SingletonMonoScope<TalentManager>.HasInstance || string.IsNullOrEmpty(skill.IndexName))
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
