using System;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using Entity.Enemies.EnemyAI;
using HarmonyLib;
using UnityEngine;

namespace PerfPatches;

/// <summary>
/// Enemy AI LOD module. Two OPT-IN patches (both Enabled = false by default):
///
/// 1) LOSRaycastCache - prefix-skip reimplementation of
///    EnemyBrain.ApplyTargetToEnemy(Transform target, bool isPlayer) (EnemyBrain.cs:408-449).
///    Ports the method verbatim (usable-check, SetBrainTarget with yao attack anchor,
///    magnitude &lt;= 0.001 early-true) but replaces the unconditional Physics2D.Raycast on
///    the "block" mask with a per-brain cached LOS result. The cache is reused only while:
///    same target Transform, age &lt;= CacheTTL, and NEITHER the enemy nor the target has
///    moved more than 0.3 units since the cached raycast (positions stored with the entry).
///    Risk: a dynamic blocker (spawned/destroyed "block" collider) can leave CanSeeTarget
///    stale for up to CacheTTL - hence opt-in.
///
/// 2) OffscreenAILOD - prefix on EnemyBrain.Tick(float dt). When the enemy's distance to
///    the player (Enemy._distToPlayer, refreshed every frame in Enemy.Update at
///    Enemy.cs:1528) exceeds ThrottleDistance, only every Nth Tick call is let through
///    (N = SkipFactor) and the skipped frames' delta time is ACCUMULATED and added to the
///    dt argument of the allowed call. Tick's internal timers (compScanTimer,
///    decisionTimer, returnLockTimer, hitBoost01 decay) are all dt-accumulator based
///    (EnemyBrain.cs:105-124), so they keep exact wall-clock cadence - decisions are
///    merely quantized to the allowed frames. EnemyA.Update (EnemyA.cs:153-176) and
///    EnemyB.Update (EnemyB.cs:255-288) both call brain.Tick(Time.deltaTime), so one
///    patch covers both; their fsm.Tick/ApplyBrainResult and JStime/JStimeA bookkeeping
///    still run every frame and are untouched.
///    Risk: far enemies react to targets/state changes up to (SkipFactor-1) frames later.
/// </summary>
internal static class EnemyAiLodModule
{
	private const string OwnerName = "EnemyAiLodModule";

	// 0.3 units of movement (squared) invalidates a cached LOS result.
	private const float LosMoveToleranceSqr = 0.09f;

	// ---- shared private-member accessors (resolved ONCE at Init) ----
	private static AccessTools.FieldRef<EnemyBrain, Enemy> _emRef;

	// ---- Patch 1: LOSRaycastCache ----
	private static ConfigEntry<bool> _losEnabled;
	private static ConfigEntry<float> _losTtl;
	private static AccessTools.FieldRef<EnemyBrain, int> _maskBlockRef;
	private static bool _losBroken;

	private sealed class LosEntry
	{
		internal bool Valid;
		internal Transform Target;
		internal float Time;
		internal Vector3 Origin;
		internal Vector3 TargetPos;
		internal bool CanSee;
	}

	// EnemyBrain is a plain sealed class (not a UnityEngine.Object); a ConditionalWeakTable
	// keys per-brain state without keeping pooled/dead brains alive. Entries are created
	// once per brain (no per-call allocation after warmup).
	private static ConditionalWeakTable<EnemyBrain, LosEntry> _losTable =
		new ConditionalWeakTable<EnemyBrain, LosEntry>();

	private static readonly ConditionalWeakTable<EnemyBrain, LosEntry>.CreateValueCallback LosEntryFactory =
		CreateLosEntry;

	private static LosEntry CreateLosEntry(EnemyBrain _)
	{
		return new LosEntry();
	}

	// ---- Patch 2: OffscreenAILOD ----
	private static ConfigEntry<bool> _lodEnabled;
	private static ConfigEntry<float> _lodDistance;
	private static ConfigEntry<int> _lodSkipFactor;
	private static AccessTools.FieldRef<Enemy, float> _distToPlayerRef;
	private static bool _lodBroken;

	private sealed class LodState
	{
		internal int Counter;
		internal float AccumDt;
	}

	private static ConditionalWeakTable<EnemyBrain, LodState> _lodTable =
		new ConditionalWeakTable<EnemyBrain, LodState>();

	private static readonly ConditionalWeakTable<EnemyBrain, LodState>.CreateValueCallback LodStateFactory =
		CreateLodState;

	private static LodState CreateLodState(EnemyBrain _)
	{
		return new LodState();
	}

	internal static void Init(ConfigFile config, Harmony harmony)
	{
		bool anyApplied = false;

		// ---------------- Patch 1: LOSRaycastCache ----------------
		try
		{
			_losEnabled = config.Bind("LOSRaycastCache", "Enabled", false,
					"OPT-IN, NARROW BENEFIT. Caches per-enemy line-of-sight raycast results " +
					"(EnemyBrain.ApplyTargetToEnemy) for CacheTTL seconds, invalidated when the enemy or " +
					"its target moves more than 0.3 units or the target Transform changes. The brain only " +
					"re-evaluates every 0.25s and the movement test invalidates almost any moving " +
					"engagement, so real savings occur mainly in standoffs where both enemy and target " +
					"stand still; CacheTTL must exceed 0.25 for the cache to ever hit. Risk: if geometry " +
					"on the 'block' layer changes (destructible wall, door), CanSeeTarget can be stale for " +
					"up to CacheTTL, delaying an attack/chase transition.");
			_losTtl = config.Bind("LOSRaycastCache", "CacheTTL", 0.6f,
				new ConfigDescription(
					"Maximum age in seconds of a cached line-of-sight result. Must be LARGER than the " +
					"brain's 0.25s decision cadence or the entry expires before it is ever consulted; " +
					"0.6 lets one result serve roughly two decisions. Higher = fewer raycasts but " +
					"staler reactions to changing geometry.",
					new AcceptableValueRange<float>(0.05f, 2f)));

			if (_emRef == null)
			{
				_emRef = AccessTools.FieldRefAccess<EnemyBrain, Enemy>("em");
			}
			_maskBlockRef = AccessTools.FieldRefAccess<EnemyBrain, int>("maskBlock");

			var applyTarget = AccessTools.Method(typeof(EnemyBrain), "ApplyTargetToEnemy",
				new[] { typeof(Transform), typeof(bool) });
			if (applyTarget == null)
			{
				throw new MissingMethodException("EnemyBrain.ApplyTargetToEnemy(Transform, bool) not found");
			}

			harmony.Patch(applyTarget,
				prefix: new HarmonyMethod(typeof(EnemyAiLodModule), nameof(ApplyTargetToEnemyPrefix)));
			anyApplied = true;
			PerfCore.Log.LogInfo($"[{OwnerName}] LOSRaycastCache patched (Enabled={_losEnabled.Value}).");
		}
		catch (Exception ex)
		{
			_losBroken = true;
			PerfCore.Log.LogError($"[{OwnerName}] LOSRaycastCache failed to initialize, patch inactive: {ex}");
		}

		// ---------------- Patch 2: OffscreenAILOD ----------------
		try
		{
			_lodEnabled = config.Bind("OffscreenAILOD", "Enabled", false,
				"OPT-IN. Reduce AI decision rate of far-away enemies: when an enemy is farther than " +
				"ThrottleDistance from the player, only every SkipFactor-th EnemyBrain.Tick call runs, " +
				"with the skipped frames' delta time accumulated into the allowed call - internal AI " +
				"timers keep exact wall-clock cadence. Movement (A* path), animation and the state " +
				"machine still update every frame. Risk: far enemies react to target/state changes up " +
				"to (SkipFactor-1) frames later.");
			_lodDistance = config.Bind("OffscreenAILOD", "ThrottleDistance", 18f,
				new ConfigDescription(
					"Distance to the player (units) beyond which enemy brains are throttled. 18 is " +
					"comfortably off-screen but below the enemy unload distance.",
					new AcceptableValueRange<float>(5f, 60f)));
			_lodSkipFactor = config.Bind("OffscreenAILOD", "SkipFactor", 2,
				new ConfigDescription(
					"Only every Nth brain tick runs for throttled enemies (2 = half rate, 4 = quarter rate).",
					new AcceptableValueRange<int>(2, 4)));

			if (_emRef == null)
			{
				_emRef = AccessTools.FieldRefAccess<EnemyBrain, Enemy>("em");
			}
			_distToPlayerRef = AccessTools.FieldRefAccess<Enemy, float>("_distToPlayer");

			var tick = AccessTools.Method(typeof(EnemyBrain), "Tick", new[] { typeof(float) });
			if (tick == null)
			{
				throw new MissingMethodException("EnemyBrain.Tick(float) not found");
			}

			harmony.Patch(tick,
				prefix: new HarmonyMethod(typeof(EnemyAiLodModule), nameof(TickPrefix)));
			anyApplied = true;
			PerfCore.Log.LogInfo($"[{OwnerName}] OffscreenAILOD patched (Enabled={_lodEnabled.Value}).");
		}
		catch (Exception ex)
		{
			_lodBroken = true;
			PerfCore.Log.LogError($"[{OwnerName}] OffscreenAILOD failed to initialize, patch inactive: {ex}");
		}

		if (anyApplied)
		{
			// Drop per-brain caches on scene unload; brains from the old scene are
			// unreachable afterwards and the tables are swapped for fresh ones
			// (ConditionalWeakTable has no Clear() on this runtime).
			PerfCore.OnSceneUnloaded(OwnerName, ClearCaches);
		}
	}

	private static void ClearCaches()
	{
		_losTable = new ConditionalWeakTable<EnemyBrain, LosEntry>();
		_lodTable = new ConditionalWeakTable<EnemyBrain, LodState>();
	}

	// ---------------------------------------------------------------
	// Patch 1: LOSRaycastCache
	// Verbatim port of EnemyBrain.ApplyTargetToEnemy (EnemyBrain.cs:408-449) with the
	// Physics2D.Raycast at :447 replaced by a movement/TTL-invalidated per-brain cache.
	// Uses:  Enemy.SetBrainTarget(Transform mvTarget, Transform atTarget, bool isPlayer)
	//        (public, Enemy.cs:1057), Enemy.CanSeeTarget (public bool field, Enemy.cs:415),
	//        Enemy.playerManager (public, Enemy.cs:31), PlayerManager.yao / Companion.yao
	//        (public GameObject). Private EnemyBrain.em / EnemyBrain.maskBlock via FieldRef.
	// Injected parameter names 'target' and 'isPlayer' match the compiled parameter names.
	// ---------------------------------------------------------------
	private static bool ApplyTargetToEnemyPrefix(EnemyBrain __instance, Transform target, bool isPlayer)
	{
		if (_losBroken || !_losEnabled.Value)
		{
			return true;
		}
		try
		{
			Enemy em = _emRef(__instance);

			// -- original null/unusable path (EnemyBrain.cs:410-421); no raycast, no cache --
			if (!IsTargetTransformUsable(target))
			{
				em.SetBrainTarget(null, null, isPlayer: false);
				em.CanSeeTarget = false;
				return false;
			}
			if (!target)
			{
				em.SetBrainTarget(null, null, isPlayer: false);
				em.CanSeeTarget = false;
				return false;
			}

			// -- original attack-anchor (yao) resolution (EnemyBrain.cs:422-438) --
			Transform atTarget = null;
			Companion component;
			if (isPlayer)
			{
				if ((bool)em.playerManager && (bool)em.playerManager.yao)
				{
					atTarget = em.playerManager.yao.transform;
				}
			}
			else if (target.TryGetComponent<Companion>(out component) && (bool)component.yao)
			{
				atTarget = component.yao.transform;
			}
			if (!atTarget)
			{
				atTarget = target;
			}
			em.SetBrainTarget(target, atTarget, isPlayer);

			// -- original distance/degenerate handling (EnemyBrain.cs:440-446) --
			Vector3 originPos = em.transform.position;
			Vector3 targetPos = target.position;
			Vector2 vector = targetPos - originPos;
			float magnitude = vector.magnitude;
			if (magnitude <= 0.001f)
			{
				em.CanSeeTarget = true;
				return false;
			}

			// -- cached replacement for the raycast at EnemyBrain.cs:447 --
			LosEntry entry = _losTable.GetValue(__instance, LosEntryFactory);
			float now = Time.unscaledTime;
			if (entry.Valid
				&& entry.Target == target
				&& now - entry.Time <= _losTtl.Value
				&& (originPos - entry.Origin).sqrMagnitude <= LosMoveToleranceSqr
				&& (targetPos - entry.TargetPos).sqrMagnitude <= LosMoveToleranceSqr)
			{
				em.CanSeeTarget = entry.CanSee;
				return false;
			}

			RaycastHit2D raycastHit2D = Physics2D.Raycast(originPos, vector.normalized, magnitude,
				_maskBlockRef(__instance));
			bool canSee = !raycastHit2D.collider;
			em.CanSeeTarget = canSee;

			entry.Valid = true;
			entry.Target = target;
			entry.Time = now;
			entry.Origin = originPos;
			entry.TargetPos = targetPos;
			entry.CanSee = canSee;
			return false;
		}
		catch (Exception ex)
		{
			// Fail-soft: the reimplementation may have partially run (SetBrainTarget already
			// applied), so never fall through to the original this call. All later calls
			// run vanilla.
			_losBroken = true;
			PerfCore.Log.LogError($"[{OwnerName}] LOSRaycastCache failed, reverting to vanilla: {ex}");
			return false;
		}
	}

	// Inline port of private static EnemyBrain.IsTargetTransformUsable (EnemyBrain.cs:610-617).
	private static bool IsTargetTransformUsable(Transform target)
	{
		if ((bool)target && target.gameObject.activeInHierarchy)
		{
			return target.gameObject.activeSelf;
		}
		return false;
	}

	// ---------------------------------------------------------------
	// Patch 2: OffscreenAILOD
	// Gate prefix on public EnemyBrain.Tick(float dt). Compiled parameter name is 'dt'
	// (EnemyBrain.cs:92); rewritten via 'ref float dt' so the allowed call receives the
	// accumulated delta time of the skipped frames - Tick's timers are accumulator-based
	// and therefore keep exact cadence. Distance source: Enemy._distToPlayer (private
	// float, Enemy.cs:450), refreshed every frame in Enemy.Update (Enemy.cs:1528) and set
	// to float.MaxValue when no PlayerManager exists - that sentinel disables throttling
	// (vanilla rate) rather than permanently throttling player-less scenes.
	// ---------------------------------------------------------------
	private static bool TickPrefix(EnemyBrain __instance, ref float dt)
	{
		if (_lodBroken || !_lodEnabled.Value)
		{
			return true;
		}
		try
		{
			LodState state = _lodTable.GetValue(__instance, LodStateFactory);
			Enemy em = _emRef(__instance);

			// Dead/destroyed enemy: let vanilla Tick handle the Die/Idle transition every
			// frame (never delay death handling). Flush any carried time first.
			if (!em || !em.IsAlive)
			{
				if (state.AccumDt > 0f)
				{
					dt += state.AccumDt;
					state.AccumDt = 0f;
				}
				state.Counter = 0;
				return true;
			}

			float dist = _distToPlayerRef(em);
			if (dist == float.MaxValue || dist <= _lodDistance.Value)
			{
				// Near (or no player): vanilla rate; hand back any time carried while far.
				if (state.AccumDt > 0f)
				{
					dt += state.AccumDt;
					state.AccumDt = 0f;
				}
				state.Counter = 0;
				return true;
			}

			// Far: let every Nth call through with the accumulated delta time.
			state.Counter++;
			if (state.Counter >= _lodSkipFactor.Value)
			{
				state.Counter = 0;
				dt += state.AccumDt;
				state.AccumDt = 0f;
				return true;
			}
			state.AccumDt += dt;
			return false;
		}
		catch (Exception ex)
		{
			// Fail-soft: skip this tick (one missed brain tick is harmless), all later
			// calls run vanilla.
			_lodBroken = true;
			PerfCore.Log.LogError($"[{OwnerName}] OffscreenAILOD failed, reverting to vanilla: {ex}");
			return false;
		}
	}
}
