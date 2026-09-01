using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace ModMenu;

/// <summary>
/// Mod Menu - a single, always-visible, translucent panel docked to the right screen border that
/// aggregates the interaction points of every other custom plugin. It replaces the pile of keyboard
/// shortcuts those plugins used to bind (the only key left in the setup is F6, which belongs to a
/// third-party plugin this mod does not touch).
///
/// Plugins join by declaring, in their own root namespace, a static class named "ModMenuProvider"
/// with a public static object[][] GetMenuItems() method. See README.md for the verbatim contract.
///
/// Contract v2 adds an OPTIONAL 5th cell per row, a Func&lt;string&gt; description that this host renders
/// as a hover tooltip to the LEFT of the panel. 4-cell rows keep working exactly as before.
///
/// This plugin binds NO hotkey on purpose: the panel IS the interaction point.
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "custom.modmenu";
    public const string PluginName = "Mod Menu";
    public const string PluginVersion = "1.0.1";

    // ---------------------------------------------------------------- constants

    private const string ProviderTypeName = "ModMenuProvider";
    private const string ProviderMethodName = "GetMenuItems";
    private const float ScanIntervalSeconds = 2f;

    // Every string the draw path can possibly need is a constant or cached, so OnGUI never
    // concatenates (OnGUI runs multiple times per frame: Layout, MouseMove, Repaint, ...).
    private const string HeaderText = "Mods";
    private const string CollapsedTabText = "M\nO\nD\nS";
    private const string EmptyRowText = "no mods registered";
    private const string OnText = "ON";
    private const string OffText = "OFF";
    private const string ActionText = ">";
    private const string LabelErrorText = "(label error)";
    private const string CollapseGlyph = "-"; // "click the header to collapse"

    private const int IconSize = 18;

    // Tooltip geometry (unscaled panel-space pixels; the whole GUI matrix is scaled by Scale).
    private const float TooltipWidth = 260f;
    private const float TooltipPad = 8f;   // inner padding on all four sides
    private const float TooltipGap = 6f;   // gap panel<->tooltip and title<->body
    private const float TooltipMargin = 2f; // minimum distance kept to any screen border

    // Hit ids used by the click tracker.
    private const int HitNone = -1;
    private const int HitHeader = -2;

    // ---------------------------------------------------------------- config

    internal static ManualLogSource Log;

    private ConfigEntry<bool> _cfgEnabled;
    private ConfigEntry<bool> _cfgCollapsed;
    private ConfigEntry<float> _cfgDockOffsetY;
    private ConfigEntry<float> _cfgWidth;
    private ConfigEntry<float> _cfgRowHeight;
    private ConfigEntry<float> _cfgOpacity;
    private ConfigEntry<float> _cfgScale;
    private ConfigEntry<bool> _cfgShowTooltips;
    private ConfigEntry<float> _cfgHoverDelay;

    // ---------------------------------------------------------------- discovery state

    /// <summary>One menu row, resolved once at discovery time; its label/state are re-read per draw.</summary>
    private sealed class Row
    {
        public string Id;
        public Func<string> Label;
        public Func<bool> State;        // null => pure action row
        public Action Click;            // may be null => display-only row
        public Func<string> Description; // contract v2, OPTIONAL: null => row has no tooltip
        public Texture2D Icon;
        public readonly GUIContent Content = new GUIContent(string.Empty);
        public string LastText;    // guards GUIContent churn
        public bool LabelBroken;   // label() threw once -> stop calling it
        public bool StateBroken;   // state() threw once -> stop calling it
        public bool ClickBroken;   // onClick() threw once -> stop calling it
        public bool DescBroken;    // description() threw once -> stop calling it
    }

    /// <summary>One discovered ModMenuProvider type.</summary>
    private sealed class Provider
    {
        public string AssemblyName;
        public string TypeName;
        public MethodInfo Method;
        public readonly List<Row> Rows = new List<Row>();
        public bool Dropped;       // invoke threw -> dropped permanently (one warning logged)
    }

    // Assemblies already inspected. GetTypes() is never called twice on the same assembly.
    private readonly HashSet<Assembly> _scannedAssemblies = new HashSet<Assembly>();
    private readonly List<Provider> _providers = new List<Provider>();
    private readonly List<Row> _rows = new List<Row>();   // flattened, deterministic order
    private float _nextScanTime;

    // Procedural icons, keyed by row id. Built once, cached for the process lifetime.
    private static readonly Dictionary<string, Texture2D> IconCache = new Dictionary<string, Texture2D>(StringComparer.Ordinal);

    // ---------------------------------------------------------------- draw state

    private bool _drawBroken;      // a fatal draw exception disables the panel rather than spamming
    private int _pressedHit = HitNone;
    private int _hoverHit = HitNone;
    private Rect _panelRect;

    // Top edge (panel space) of the row currently under the pointer; only meaningful when _hoverHit >= 0.
    private float _hoverRowTop;

    // ---- tooltip state
    // Which row the hover timer is currently counting for, and since when (unscaled -> works while paused).
    private int _tipHoverHit = HitNone;
    private string _tipHoverId;
    private float _tipHoverSince;

    // Cached, measured tooltip content. description() is called only when this cache is (re)built:
    // once per hovered-row change, plus a cheap re-read on Layout events so a live description can
    // update. CalcHeight only runs when the text (or the wrap width) actually changed.
    private string _tipCachedId;
    private string _tipTitleText;
    private string _tipBodyText;
    private float _tipCachedWidth = -1f;
    private float _tipTitleHeight;
    private float _tipBodyHeight;
    private bool _tipHasText;
    private readonly GUIContent _tipTitleContent = new GUIContent(string.Empty);
    private readonly GUIContent _tipBodyContent = new GUIContent(string.Empty);

    private static Texture2D _texPanel;
    private static Texture2D _texHeader;
    private static Texture2D _texRow;
    private static Texture2D _texRowHover;
    private static Texture2D _texOn;
    private static Texture2D _texOff;
    private static Texture2D _texAction;
    private static Texture2D _texEdge;
    private static Texture2D _texTooltip;

    private GUIStyle _styleHeader;
    private GUIStyle _styleHeaderGlyph;
    private GUIStyle _styleRow;
    private GUIStyle _styleRowDim;
    private GUIStyle _stylePill;
    private GUIStyle _styleTab;
    private GUIStyle _styleTipTitle;
    private GUIStyle _styleTipBody;

    private static readonly GUIContent HeaderContent = new GUIContent(HeaderText);
    private static readonly GUIContent HeaderGlyphContent = new GUIContent(CollapseGlyph);
    private static readonly GUIContent TabContent = new GUIContent(CollapsedTabText);
    private static readonly GUIContent EmptyContent = new GUIContent(EmptyRowText);
    private static readonly GUIContent OnContent = new GUIContent(OnText);
    private static readonly GUIContent OffContent = new GUIContent(OffText);
    private static readonly GUIContent ActionContent = new GUIContent(ActionText);

    // Overflow indicator ("+3 more"): rebuilt only when the hidden count changes.
    private readonly GUIContent _moreContent = new GUIContent(string.Empty);
    private int _moreCount = -1;

    // ---------------------------------------------------------------- lifecycle

    private void Awake()
    {
        Log = base.Logger;

        _cfgEnabled = base.Config.Bind("General", "Enabled", true,
            "Master switch for the docked Mod Menu panel. When false nothing is drawn and no assemblies are scanned.");
        _cfgCollapsed = base.Config.Bind("General", "Collapsed", false,
            "Persisted collapse state. When collapsed only a narrow vertical tab is shown on the right border. Click the tab (or the 'Mods' header) to toggle.");
        _cfgDockOffsetY = base.Config.Bind("Layout", "DockOffsetY", 0f,
            "Vertical offset in pixels from the vertically centered dock position. 0 = centered, negative = up, positive = down. The panel is always clamped to stay on screen.");
        _cfgWidth = base.Config.Bind("Layout", "Width", 190f,
            new ConfigDescription("Panel width in (scaled) pixels.", new AcceptableValueRange<float>(120f, 420f)));
        _cfgRowHeight = base.Config.Bind("Layout", "RowHeight", 30f,
            new ConfigDescription("Height of one menu row in (scaled) pixels.", new AcceptableValueRange<float>(18f, 60f)));
        _cfgOpacity = base.Config.Bind("Appearance", "Opacity", 0.55f,
            new ConfigDescription("Global alpha of the whole panel so the game stays visible through it.", new AcceptableValueRange<float>(0.15f, 1f)));
        _cfgScale = base.Config.Bind("Appearance", "Scale", 1f,
            new ConfigDescription("Uniform UI scale of the panel.", new AcceptableValueRange<float>(0.75f, 2f)));
        _cfgShowTooltips = base.Config.Bind("Tooltips", "ShowTooltips", true,
            "Show a description tooltip to the left of the panel while the pointer rests on a row. Rows whose plugin supplies no description never show one.");
        _cfgHoverDelay = base.Config.Bind("Tooltips", "HoverDelaySeconds", 0.35f,
            new ConfigDescription("How long the pointer must rest on the same row before its tooltip appears. 0 = instantly. Measured in unscaled time, so it also works while the game is paused.",
                new AcceptableValueRange<float>(0f, 2f)));

        // Scan on the very first Update so providers loaded after us are still found.
        _nextScanTime = 0f;

        Log.LogInfo("Mod Menu loaded. No hotkey is bound - the panel docked to the right screen border is the single point of interaction.");
    }

    private void Update()
    {
        if (_cfgEnabled == null || !_cfgEnabled.Value)
            return;

        float now = Time.unscaledTime;
        if (now < _nextScanTime)
            return;
        _nextScanTime = now + ScanIntervalSeconds;

        try
        {
            ScanForProviders();
        }
        catch (Exception ex)
        {
            Log.LogWarning("Mod Menu: provider scan failed: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- discovery

    /// <summary>
    /// Inspects only assemblies never inspected before (plugin load order is not guaranteed, so this
    /// runs periodically instead of once), then invokes any provider that has not yielded rows yet.
    /// </summary>
    private void ScanForProviders()
    {
        Assembly[] assemblies;
        try
        {
            assemblies = AppDomain.CurrentDomain.GetAssemblies();
        }
        catch (Exception ex)
        {
            Log.LogWarning("Mod Menu: could not enumerate assemblies: " + ex.Message);
            return;
        }

        bool foundNewProvider = false;

        for (int i = 0; i < assemblies.Length; i++)
        {
            Assembly asm = assemblies[i];
            if (asm == null)
                continue;

            // HashSet.Add returns false for assemblies we already looked at: GetTypes() runs at most
            // once per assembly for the whole process lifetime.
            if (!_scannedAssemblies.Add(asm))
                continue;

            try
            {
                if (asm.IsDynamic || asm.ReflectionOnly)
                    continue;
            }
            catch
            {
                continue;
            }

            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException rtle)
            {
                // Partially loadable assembly: use whatever types did resolve.
                types = rtle.Types;
            }
            catch (Exception ex)
            {
                Log.LogDebug("Mod Menu: skipped assembly (GetTypes failed): " + SafeAssemblyName(asm) + " - " + ex.Message);
                continue;
            }

            if (types == null)
                continue;

            for (int t = 0; t < types.Length; t++)
            {
                Type type = types[t];
                if (type == null)
                    continue;

                try
                {
                    if (!string.Equals(type.Name, ProviderTypeName, StringComparison.Ordinal))
                        continue;

                    // "static class" in C# == abstract + sealed at the IL level.
                    if (!(type.IsClass && type.IsAbstract && type.IsSealed))
                        continue;

                    MethodInfo method = type.GetMethod(ProviderMethodName, BindingFlags.Public | BindingFlags.Static);
                    if (method == null || method.GetParameters().Length != 0)
                    {
                        Log.LogWarning("Mod Menu: type " + type.FullName + " in " + SafeAssemblyName(asm) +
                                       " is named " + ProviderTypeName + " but has no public static " + ProviderMethodName + "() - ignored.");
                        continue;
                    }

                    _providers.Add(new Provider
                    {
                        AssemblyName = SafeAssemblyName(asm),
                        TypeName = type.FullName ?? type.Name,
                        Method = method
                    });
                    foundNewProvider = true;
                }
                catch (Exception ex)
                {
                    Log.LogDebug("Mod Menu: type inspection failed in " + SafeAssemblyName(asm) + ": " + ex.Message);
                }
            }
        }

        // Invoke providers that have not produced rows yet. A provider whose subsystem is not ready
        // may legitimately return zero rows early on; retrying costs one delegate call every 2s and
        // does NOT require touching GetTypes() again.
        bool rowsChanged = foundNewProvider;
        for (int i = 0; i < _providers.Count; i++)
        {
            Provider p = _providers[i];
            if (p.Dropped || p.Rows.Count > 0)
                continue;
            if (InvokeProvider(p))
                rowsChanged = true;
        }

        if (rowsChanged)
            RebuildRowOrder();
    }

    /// <summary>Invokes one provider and materialises its rows. Returns true if rows were added.</summary>
    private bool InvokeProvider(Provider p)
    {
        object raw;
        try
        {
            raw = p.Method.Invoke(null, null);
        }
        catch (Exception ex)
        {
            // ONE warning, then the provider is dropped for good.
            p.Dropped = true;
            Exception real = (ex as TargetInvocationException)?.InnerException ?? ex;
            Log.LogWarning("Mod Menu: provider " + p.TypeName + " (" + p.AssemblyName + ") threw from " +
                           ProviderMethodName + "() and was dropped: " + real.Message);
            return false;
        }

        if (raw == null)
            return false;

        Array outer = raw as Array;
        if (outer == null)
        {
            p.Dropped = true;
            Log.LogWarning("Mod Menu: provider " + p.TypeName + " (" + p.AssemblyName + ") returned " +
                           raw.GetType().FullName + " instead of object[][] and was dropped.");
            return false;
        }

        int added = 0;
        for (int i = 0; i < outer.Length; i++)
        {
            object[] cells;
            try
            {
                cells = outer.GetValue(i) as object[];
            }
            catch
            {
                continue;
            }

            if (cells == null || cells.Length < 4)
                continue;

            string id = cells[0] as string;
            Func<string> label = cells[1] as Func<string>;
            Func<bool> state = cells[2] as Func<bool>;   // may legitimately be null
            Action click = cells[3] as Action;

            // Contract v2: cell [4] is an OPTIONAL Func<string> description used as a hover tooltip.
            // A 4-cell row, or a 5th cell that is null / not a Func<string>, simply means "no tooltip";
            // that is not an error and is never logged, so v1 providers stay silent and fully working.
            Func<string> description = null;
            if (cells.Length >= 5)
                description = cells[4] as Func<string>;

            if (string.IsNullOrEmpty(id) || label == null)
            {
                Log.LogWarning("Mod Menu: provider " + p.TypeName + " (" + p.AssemblyName + ") row " + i +
                               " is malformed (needs string id + Func<string> label) - row skipped.");
                continue;
            }

            p.Rows.Add(new Row
            {
                Id = id,
                Label = label,
                State = state,
                Click = click,
                Description = description,
                Icon = null // built lazily on first draw
            });
            added++;
        }

        if (added > 0)
            Log.LogInfo("Mod Menu: registered " + added + " row(s) from " + p.TypeName + " (" + p.AssemblyName + ").");

        return added > 0;
    }

    /// <summary>
    /// Deterministic order: group by provider assembly name (ordinal), then provider type name, then
    /// the order the provider itself returned its rows. The menu therefore never reshuffles between
    /// sessions, regardless of assembly load order.
    /// </summary>
    private void RebuildRowOrder()
    {
        _providers.Sort(CompareProviders);

        _rows.Clear();
        for (int i = 0; i < _providers.Count; i++)
        {
            Provider p = _providers[i];
            if (p.Dropped)
                continue;
            for (int r = 0; r < p.Rows.Count; r++)
                _rows.Add(p.Rows[r]);
        }

        _pressedHit = HitNone;

        // Row indices just moved: drop the hover timer and the measured tooltip cache so nothing is
        // attributed to the wrong row for a frame.
        _tipHoverHit = HitNone;
        _tipHoverId = null;
        _tipHoverSince = Time.unscaledTime;
        _tipCachedId = null;
        _tipHasText = false;
    }

    private static int CompareProviders(Provider a, Provider b)
    {
        int c = string.Compare(a.AssemblyName, b.AssemblyName, StringComparison.Ordinal);
        if (c != 0)
            return c;
        return string.Compare(a.TypeName, b.TypeName, StringComparison.Ordinal);
    }

    private static string SafeAssemblyName(Assembly asm)
    {
        try
        {
            return asm.GetName().Name ?? "<unnamed>";
        }
        catch
        {
            return "<unnamed>";
        }
    }

    // ---------------------------------------------------------------- drawing

    private void OnGUI()
    {
        if (_drawBroken || _cfgEnabled == null || !_cfgEnabled.Value)
            return;

        Event e = Event.current;
        if (e == null)
            return;

        Matrix4x4 prevMatrix = GUI.matrix;
        Color prevColor = GUI.color;
        int prevDepth = GUI.depth;

        try
        {
            EnsureResources();

            float scale = Mathf.Clamp(_cfgScale.Value, 0.75f, 2f);
            float opacity = Mathf.Clamp(_cfgOpacity.Value, 0.15f, 1f);

            // Uniform scale for the whole panel. Event.current.mousePosition is reported in the
            // current GUI matrix space, so hit testing below stays correct without manual division.
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            GUI.depth = -100; // negative depth = drawn in front of other IMGUI overlays

            // Single global alpha multiplier: every texture and every label of the panel is tinted by
            // it, which is exactly the "the game stays visible through the menu" requirement.
            GUI.color = new Color(1f, 1f, 1f, opacity);

            float screenW = Screen.width / scale;
            float screenH = Screen.height / scale;

            bool collapsed = _cfgCollapsed.Value;
            if (collapsed)
                DrawCollapsed(e, screenW, screenH);
            else
                DrawExpanded(e, screenW, screenH);

            // Tooltip last: drawn after the panel in the very same OnGUI pass, so IMGUI's immediate
            // draw order guarantees it sits on top of the panel it points at (no depth juggling).
            MaintainTooltip(e, screenH, opacity, collapsed);
        }
        catch (Exception ex)
        {
            _drawBroken = true;
            Log.LogError("Mod Menu: draw failed, panel disabled for this session: " + ex);
        }
        finally
        {
            // Always restore global GUI state - leaking it would tint/scale every other IMGUI drawer.
            GUI.color = prevColor;
            GUI.matrix = prevMatrix;
            GUI.depth = prevDepth;
        }
    }

    private void DrawCollapsed(Event e, float screenW, float screenH)
    {
        float rowH = Mathf.Clamp(_cfgRowHeight.Value, 18f, 60f);
        float tabW = 20f;
        float tabH = Mathf.Max(72f, rowH * 3f);

        float y = Mathf.Clamp((screenH - tabH) * 0.5f + _cfgDockOffsetY.Value, 0f, Mathf.Max(0f, screenH - tabH));
        _panelRect = new Rect(screenW - tabW, y, tabW, tabH);

        Vector2 mouse = e.mousePosition;
        bool inside = _panelRect.Contains(mouse);
        _hoverHit = inside ? HitHeader : HitNone;

        HandleMouse(e, inside);

        if (e.type != EventType.Repaint)
            return;

        GUI.DrawTexture(_panelRect, inside ? _texRowHover : _texHeader);
        GUI.DrawTexture(new Rect(_panelRect.x, _panelRect.y, 1f, _panelRect.height), _texEdge);
        GUI.Label(_panelRect, TabContent, _styleTab);
    }

    private void DrawExpanded(Event e, float screenW, float screenH)
    {
        float pad = 4f;
        float width = Mathf.Clamp(_cfgWidth.Value, 120f, 420f);
        float rowH = Mathf.Clamp(_cfgRowHeight.Value, 18f, 60f);
        float headerH = rowH;

        int rowCount = _rows.Count;
        bool empty = rowCount == 0;
        int slots = empty ? 1 : rowCount;

        // How many rows physically fit between the screen edges?
        float chrome = pad * 2f + headerH;
        int maxSlots = Mathf.Max(1, Mathf.FloorToInt((screenH - chrome) / rowH));
        int hidden = 0;
        if (slots > maxSlots)
        {
            hidden = slots - (maxSlots - 1); // last slot becomes the "+N more" indicator
            slots = maxSlots;
        }

        float panelH = chrome + slots * rowH;
        float y = Mathf.Clamp((screenH - panelH) * 0.5f + _cfgDockOffsetY.Value, 0f, Mathf.Max(0f, screenH - panelH));
        _panelRect = new Rect(screenW - width, y, width, panelH);

        Vector2 mouse = e.mousePosition;
        bool inside = _panelRect.Contains(mouse);

        Rect headerRect = new Rect(_panelRect.x + pad, _panelRect.y + pad, width - pad * 2f, headerH - pad);
        float firstRowY = _panelRect.y + pad + headerH;

        // ---- hit test (done for every event type so hover works on Repaint too)
        _hoverHit = HitNone;
        _hoverRowTop = firstRowY;
        if (inside)
        {
            if (headerRect.Contains(mouse))
            {
                _hoverHit = HitHeader;
            }
            else if (!empty)
            {
                int idx = Mathf.FloorToInt((mouse.y - firstRowY) / rowH);
                int drawable = hidden > 0 ? slots - 1 : slots;
                if (idx >= 0 && idx < drawable && idx < rowCount)
                {
                    _hoverHit = idx;
                    // Same arithmetic the row loop below uses, so the tooltip lines up with the
                    // highlighted row exactly. Reused instead of re-deriving it in the tooltip code.
                    _hoverRowTop = firstRowY + idx * rowH;
                }
            }
        }

        HandleMouse(e, inside);

        if (e.type != EventType.Repaint)
            return;

        // ---- panel background + left edge highlight
        GUI.DrawTexture(_panelRect, _texPanel);
        GUI.DrawTexture(new Rect(_panelRect.x, _panelRect.y, 1f, _panelRect.height), _texEdge);

        // ---- header (click target: collapses / expands)
        GUI.DrawTexture(headerRect, _hoverHit == HitHeader ? _texRowHover : _texHeader);
        GUI.Label(new Rect(headerRect.x + 6f, headerRect.y, headerRect.width - 26f, headerRect.height), HeaderContent, _styleHeader);
        GUI.Label(new Rect(headerRect.xMax - 20f, headerRect.y, 18f, headerRect.height), HeaderGlyphContent, _styleHeaderGlyph);

        if (empty)
        {
            // Diagnostic row: the header alone would look like a bug.
            Rect r = new Rect(_panelRect.x + pad, firstRowY, width - pad * 2f, rowH);
            GUI.DrawTexture(r, _texRow);
            GUI.Label(new Rect(r.x + 8f, r.y, r.width - 12f, r.height), EmptyContent, _styleRowDim);
            return;
        }

        int visible = hidden > 0 ? slots - 1 : slots;
        for (int i = 0; i < visible && i < rowCount; i++)
            DrawRow(_rows[i], new Rect(_panelRect.x + pad, firstRowY + i * rowH, width - pad * 2f, rowH), i == _hoverHit);

        if (hidden > 0)
        {
            if (_moreCount != hidden)
            {
                _moreCount = hidden;
                _moreContent.text = "+" + hidden.ToString() + " more (raise screen height / lower RowHeight)";
            }
            Rect r = new Rect(_panelRect.x + pad, firstRowY + visible * rowH, width - pad * 2f, rowH);
            GUI.DrawTexture(r, _texRow);
            GUI.Label(new Rect(r.x + 8f, r.y, r.width - 12f, r.height), _moreContent, _styleRowDim);
        }
    }

    private void DrawRow(Row row, Rect rect, bool hovered)
    {
        GUI.DrawTexture(rect, hovered ? _texRowHover : _texRow);

        // Icon (procedural, cached per row id).
        if (row.Icon == null)
            row.Icon = GetIcon(row.Id);
        float iconY = rect.y + (rect.height - IconSize) * 0.5f;
        Rect iconRect = new Rect(rect.x + 5f, iconY, IconSize, IconSize);
        if (row.Icon != null)
            GUI.DrawTexture(iconRect, row.Icon);

        // State pill on the right.
        float pillW = 30f;
        float pillH = Mathf.Min(15f, rect.height - 8f);
        Rect pillRect = new Rect(rect.xMax - pillW - 5f, rect.y + (rect.height - pillH) * 0.5f, pillW, pillH);

        if (row.State != null)
        {
            bool on = false;
            if (!row.StateBroken)
            {
                try
                {
                    on = row.State();
                }
                catch (Exception ex)
                {
                    row.StateBroken = true;
                    Log.LogWarning("Mod Menu: state() of row '" + row.Id + "' threw and will no longer be queried: " + ex.Message);
                }
            }
            GUI.DrawTexture(pillRect, on ? _texOn : _texOff);
            GUI.Label(pillRect, on ? OnContent : OffContent, _stylePill);
        }
        else
        {
            // Pure action: a small chevron instead of an ON/OFF pill.
            GUI.DrawTexture(new Rect(pillRect.xMax - 12f, pillRect.y, 12f, pillRect.height), _texAction);
            GUI.Label(new Rect(pillRect.xMax - 12f, pillRect.y, 12f, pillRect.height), ActionContent, _stylePill);
        }

        // Label. The GUIContent is reused; its text is only written when the provider's label changed,
        // so a steady-state frame allocates nothing here.
        string text;
        if (row.LabelBroken)
        {
            text = LabelErrorText;
        }
        else
        {
            try
            {
                text = row.Label() ?? row.Id;
            }
            catch (Exception ex)
            {
                row.LabelBroken = true;
                text = LabelErrorText;
                Log.LogWarning("Mod Menu: label() of row '" + row.Id + "' threw and will no longer be called: " + ex.Message);
            }
        }

        if (!ReferenceEquals(text, row.LastText) && !string.Equals(text, row.LastText, StringComparison.Ordinal))
        {
            row.LastText = text;
            row.Content.text = text;
        }

        Rect labelRect = new Rect(iconRect.xMax + 6f, rect.y, pillRect.x - iconRect.xMax - 10f, rect.height);
        GUI.Label(labelRect, row.Content, row.Click == null ? _styleRowDim : _styleRow);
    }

    // ---------------------------------------------------------------- tooltip (contract v2)

    /// <summary>
    /// Runs once per OnGUI pass, AFTER the panel was drawn. Keeps the hover timer up to date for every
    /// event type, (re)builds the measured tooltip cache at most once per hovered-row change, and draws
    /// the tooltip on Repaint only.
    /// </summary>
    private void MaintainTooltip(Event e, float screenH, float opacity, bool collapsed)
    {
        // The timer must advance/reset on every event type (MouseMove is what usually changes the hover),
        // and it must be reset while collapsed so re-expanding does not instantly pop a stale tooltip.
        UpdateHoverTimer(collapsed ? HitNone : _hoverHit);

        if (_cfgShowTooltips == null || !_cfgShowTooltips.Value)
            return;
        if (collapsed)
            return;

        int hit = _hoverHit;
        if (hit < 0 || hit >= _rows.Count)
            return;

        Row row = _rows[hit];
        // A row without a description (4-cell contract-v1 row, or one whose description() already threw)
        // simply has no tooltip.
        if (row.Description == null || row.DescBroken)
            return;

        float delay = Mathf.Clamp(_cfgHoverDelay.Value, 0f, 2f);
        if (Time.unscaledTime - _tipHoverSince < delay)
            return;

        float innerWidth = TooltipWidth - TooltipPad * 2f;
        // allowRefresh only on Layout: that is one event per frame, so a description that changes over
        // time still updates, but Repaint/MouseMove never call the provider delegate or CalcHeight.
        EnsureTooltipCache(row, innerWidth, e.type == EventType.Layout);

        if (!_tipHasText)
            return;
        if (e.type != EventType.Repaint)
            return;

        DrawTooltip(screenH, opacity, innerWidth);
    }

    /// <summary>Resets the rest-timer (and the measured cache) whenever the hovered row changes.</summary>
    private void UpdateHoverTimer(int hit)
    {
        string id = hit >= 0 && hit < _rows.Count ? _rows[hit].Id : null;
        if (hit == _tipHoverHit && string.Equals(id, _tipHoverId, StringComparison.Ordinal))
            return;

        _tipHoverHit = hit;
        _tipHoverId = id;
        _tipHoverSince = Time.unscaledTime;

        // Invalidate: the next eligible pass rebuilds and re-measures for the new row.
        _tipCachedId = null;
        _tipHasText = false;
    }

    /// <summary>
    /// Calls description() and measures the wrapped height. No-op unless the row changed, the wrap width
    /// changed, or a Layout event allowed a re-read that produced different text.
    /// </summary>
    private void EnsureTooltipCache(Row row, float innerWidth, bool allowRefresh)
    {
        bool rowChanged = !string.Equals(_tipCachedId, row.Id, StringComparison.Ordinal);
        bool widthChanged = _tipCachedWidth != innerWidth;
        if (!rowChanged && !widthChanged && !allowRefresh)
            return;

        string body = null;
        try
        {
            body = row.Description();
        }
        catch (Exception ex)
        {
            row.DescBroken = true;
            body = null;
            if (Log != null)
                Log.LogWarning("Mod Menu: description() of row '" + row.Id +
                               "' threw and will no longer be called: " + ex.Message);
        }

        if (body != null && body.Length == 0)
            body = null;

        // Title = whatever the row itself currently shows; falls back to the id before the first Repaint.
        string title = row.LastText;
        if (string.IsNullOrEmpty(title))
            title = row.Id;

        _tipCachedId = row.Id;
        _tipCachedWidth = innerWidth;
        _tipHasText = body != null;

        bool textChanged = rowChanged || widthChanged ||
                           !string.Equals(body, _tipBodyText, StringComparison.Ordinal) ||
                           !string.Equals(title, _tipTitleText, StringComparison.Ordinal);
        if (!textChanged)
            return;

        _tipBodyText = body;
        _tipTitleText = title;
        _tipTitleContent.text = title;
        _tipBodyContent.text = body ?? string.Empty;

        // The ONLY place CalcHeight runs. Same style + same width as the GUI.Label below, so the box can
        // never clip the wrapped text.
        _tipTitleHeight = _styleTipTitle.CalcHeight(_tipTitleContent, innerWidth);
        _tipBodyHeight = body == null ? 0f : _styleTipBody.CalcHeight(_tipBodyContent, innerWidth);
    }

    /// <summary>
    /// Draws the cached tooltip to the LEFT of the right-docked panel, clamped fully on screen. Repaint
    /// only; every value used here is precomputed, so this path allocates nothing.
    /// </summary>
    private void DrawTooltip(float screenH, float opacity, float innerWidth)
    {
        float height = TooltipPad * 2f + _tipTitleHeight + TooltipGap + _tipBodyHeight;

        // Horizontal: left of the panel; if the panel is wide enough to push us off the left border,
        // pin to the border instead (the panel is translucent, so overlap stays readable).
        float x = _panelRect.x - TooltipGap - TooltipWidth;
        if (x < TooltipMargin)
            x = TooltipMargin;

        // Vertical: align the top with the hovered row, then clamp so the whole box stays on screen.
        float maxY = screenH - height - TooltipMargin;
        float y = _hoverRowTop;
        if (y > maxY)
            y = maxY;
        if (y < TooltipMargin)
            y = TooltipMargin;

        Rect box = new Rect(x, y, TooltipWidth, height);

        // Same translucent treatment as the panel (one global alpha multiplier) but deliberately more
        // opaque than the rows, so wrapped body text stays readable over a busy scene.
        Color prev = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(opacity + 0.35f));
        try
        {
            GUI.DrawTexture(box, _texTooltip);
            GUI.DrawTexture(new Rect(box.x, box.y, 1f, box.height), _texEdge);
            GUI.DrawTexture(new Rect(box.x, box.y, box.width, 1f), _texEdge);

            GUI.Label(new Rect(box.x + TooltipPad, box.y + TooltipPad, innerWidth, _tipTitleHeight),
                      _tipTitleContent, _styleTipTitle);
            GUI.Label(new Rect(box.x + TooltipPad, box.y + TooltipPad + _tipTitleHeight + TooltipGap, innerWidth, _tipBodyHeight),
                      _tipBodyContent, _styleTipBody);
        }
        finally
        {
            GUI.color = prev;
        }
    }

    // ---------------------------------------------------------------- input

    /// <summary>
    /// Mouse-only interaction. Press records the hit, release on the same hit activates it.
    /// </summary>
    private void HandleMouse(Event e, bool insidePanel)
    {
        switch (e.type)
        {
            case EventType.MouseDown:
                if (!insidePanel)
                {
                    _pressedHit = HitNone;
                    return;
                }
                _pressedHit = _hoverHit;
                // WHY Use(): the pointer is over our panel, so this click belongs to the menu only.
                // Marking the event used stops IMGUI from handing the same click to anything else that
                // draws later in the same frame (other plugin windows/overlays and the default GUI
                // handling), so a click on a row cannot simultaneously act as a click "through" the
                // panel. It also prevents IMGUI from starting a drag/selection behind the panel.
                e.Use();
                return;

            case EventType.MouseUp:
                if (!insidePanel)
                {
                    _pressedHit = HitNone;
                    return;
                }
                int hit = _hoverHit;
                int pressed = _pressedHit;
                _pressedHit = HitNone;
                // Use() before invoking: whatever the action does (opening windows, toggling state) must
                // not see this click again, and the release must not reach the game either.
                e.Use();
                if (pressed != HitNone && pressed == hit)
                    Activate(hit);
                return;

            case EventType.MouseDrag:
            case EventType.ScrollWheel:
                // Swallowed for the same reason: no camera panning / zooming while the cursor is on the
                // menu. (The panel is docked by design, so drags do nothing else.)
                if (insidePanel)
                    e.Use();
                return;
        }
    }

    private void Activate(int hit)
    {
        if (hit == HitHeader)
        {
            _cfgCollapsed.Value = !_cfgCollapsed.Value; // persisted by BepInEx on set
            return;
        }

        if (hit < 0 || hit >= _rows.Count)
            return;

        Row row = _rows[hit];
        if (row.Click == null || row.ClickBroken)
            return;

        try
        {
            row.Click();
        }
        catch (Exception ex)
        {
            row.ClickBroken = true;
            Log.LogWarning("Mod Menu: onClick() of row '" + row.Id + "' threw and the row was disabled: " + ex);
        }
    }

    // ---------------------------------------------------------------- resources

    private void EnsureResources()
    {
        if (_texPanel == null)
            _texPanel = MakeFill(new Color(0.055f, 0.06f, 0.075f, 0.88f));
        if (_texHeader == null)
            _texHeader = MakeFill(new Color(0.16f, 0.17f, 0.21f, 0.95f));
        if (_texRow == null)
            _texRow = MakeFill(new Color(0.13f, 0.14f, 0.17f, 0.75f));
        if (_texRowHover == null)
            _texRowHover = MakeFill(new Color(0.32f, 0.35f, 0.42f, 0.95f));
        if (_texOn == null)
            _texOn = MakeFill(new Color(0.18f, 0.68f, 0.30f, 0.95f));
        if (_texOff == null)
            _texOff = MakeFill(new Color(0.30f, 0.31f, 0.34f, 0.75f));
        if (_texAction == null)
            _texAction = MakeFill(new Color(0.35f, 0.42f, 0.55f, 0.55f));
        if (_texEdge == null)
            _texEdge = MakeFill(new Color(0.75f, 0.80f, 0.95f, 0.60f));
        // Darker and nearly fully opaque: combined with the boosted GUI.color alpha in DrawTooltip this
        // reads clearly against bright scenes while still being the same flat-fill treatment as the panel.
        if (_texTooltip == null)
            _texTooltip = MakeFill(new Color(0.035f, 0.04f, 0.055f, 0.97f));

        if (_styleRow != null)
            return;

        // Styles are built once, on the first OnGUI (GUI.skin is only valid inside OnGUI).
        GUIStyle baseLabel = GUI.skin != null && GUI.skin.label != null ? new GUIStyle(GUI.skin.label) : new GUIStyle();

        _styleRow = new GUIStyle(baseLabel)
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize = 12,
            wordWrap = false,
            clipping = TextClipping.Clip,
            padding = new RectOffset(0, 0, 0, 0),
            margin = new RectOffset(0, 0, 0, 0)
        };
        _styleRow.normal.textColor = new Color(0.96f, 0.97f, 1f, 1f);
        _styleRow.normal.background = null;

        _styleRowDim = new GUIStyle(_styleRow);
        _styleRowDim.normal.textColor = new Color(0.66f, 0.68f, 0.74f, 1f);

        _styleHeader = new GUIStyle(_styleRow) { fontStyle = FontStyle.Bold, fontSize = 13 };
        _styleHeader.normal.textColor = new Color(1f, 1f, 1f, 1f);

        _styleHeaderGlyph = new GUIStyle(_styleHeader) { alignment = TextAnchor.MiddleCenter };

        _stylePill = new GUIStyle(_styleRow) { alignment = TextAnchor.MiddleCenter, fontSize = 9, fontStyle = FontStyle.Bold };
        _stylePill.normal.textColor = new Color(1f, 1f, 1f, 1f);

        _styleTab = new GUIStyle(_styleRow) { alignment = TextAnchor.MiddleCenter, fontSize = 10, fontStyle = FontStyle.Bold, wordWrap = true };
        _styleTab.normal.textColor = new Color(0.95f, 0.96f, 1f, 1f);

        // Tooltip styles. wordWrap MUST be true for CalcHeight to report a wrapped multi-line height,
        // and clipping stays Clip because the box is sized from exactly these styles at exactly the same
        // width, so nothing can actually be cut off.
        _styleTipTitle = new GUIStyle(_styleRow)
        {
            alignment = TextAnchor.UpperLeft,
            fontSize = 12,
            fontStyle = FontStyle.Bold,
            wordWrap = true,
            clipping = TextClipping.Clip
        };
        _styleTipTitle.normal.textColor = new Color(1f, 1f, 1f, 1f);

        _styleTipBody = new GUIStyle(_styleTipTitle) { fontStyle = FontStyle.Normal, fontSize = 11 };
        _styleTipBody.normal.textColor = new Color(0.80f, 0.83f, 0.90f, 1f);
    }

    /// <summary>1x1 texture used for every background fill: alpha is exact and predictable, unlike GUI.skin.box.</summary>
    private static Texture2D MakeFill(Color c)
    {
        Texture2D tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tex.hideFlags = HideFlags.HideAndDontSave; // survives scene loads, never shows up in the hierarchy
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.SetPixel(0, 0, c);
        tex.Apply(false, false);
        return tex;
    }

    private static Texture2D GetIcon(string id)
    {
        Texture2D tex;
        if (IconCache.TryGetValue(id, out tex) && tex != null)
            return tex;

        try
        {
            tex = BuildIcon(id);
        }
        catch (Exception ex)
        {
            if (Log != null)
                Log.LogWarning("Mod Menu: could not build icon for row '" + id + "': " + ex.Message);
            // Cache a transparent stand-in so the failing generator is never retried per frame.
            tex = MakeFill(new Color(1f, 1f, 1f, 0f));
        }

        IconCache[id] = tex;
        return tex;
    }

    /// <summary>
    /// Procedurally builds a small icon (no external assets). Shape and hue are derived from a stable
    /// hash of the row id, so a given row always gets the same glyph across sessions.
    /// </summary>
    private static Texture2D BuildIcon(string id)
    {
        int hash = StableHash(id);
        int shape = (hash & 0x7FFFFFFF) % 6;
        float hue = ((hash >> 5) & 0x7FFFFFFF) % 360 / 360f;

        Color body = Color.HSVToRGB(hue, 0.55f, 1f);
        Color32 fg = new Color32((byte)(body.r * 255f), (byte)(body.g * 255f), (byte)(body.b * 255f), 255);
        Color32 dim = new Color32((byte)(body.r * 140f), (byte)(body.g * 140f), (byte)(body.b * 140f), 190);
        Color32 clear = new Color32(0, 0, 0, 0);

        int n = IconSize;
        Color32[] px = new Color32[n * n];
        float c = (n - 1) * 0.5f;

        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float dx = x - c;
                float dy = y - c;
                bool hit;
                bool outline = false;

                switch (shape)
                {
                    case 0: // filled circle
                        hit = dx * dx + dy * dy <= 7.2f * 7.2f;
                        break;
                    case 1: // rounded square
                        hit = Mathf.Abs(dx) <= 6.5f && Mathf.Abs(dy) <= 6.5f &&
                              (Mathf.Abs(dx) + Mathf.Abs(dy)) <= 11.5f;
                        break;
                    case 2: // triangle pointing up (+y is up in texture space)
                        hit = dy >= -6.5f && dy <= 7f && Mathf.Abs(dx) <= (7f - dy) * 0.55f;
                        break;
                    case 3: // diamond
                        hit = Mathf.Abs(dx) + Mathf.Abs(dy) <= 7.5f;
                        break;
                    case 4: // ring
                    {
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        hit = d <= 7.5f && d >= 4.3f;
                        break;
                    }
                    default: // three horizontal bars
                    {
                        int band = y / 3;
                        hit = Mathf.Abs(dx) <= 7f && (band == 1 || band == 3 || band == 5);
                        break;
                    }
                }

                if (!hit)
                {
                    // Thin dim border so the icon reads on bright scenes too.
                    outline = (x == 0 || y == 0 || x == n - 1 || y == n - 1) && shape == 1;
                    px[y * n + x] = outline ? dim : clear;
                }
                else
                {
                    px[y * n + x] = fg;
                }
            }
        }

        Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.hideFlags = HideFlags.HideAndDontSave;
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.SetPixels32(px);
        tex.Apply(false, false);
        return tex;
    }

    /// <summary>FNV-1a. string.GetHashCode() is not guaranteed stable across runtimes; this is.</summary>
    private static int StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261u;
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 16777619u;
            }
            return (int)h;
        }
    }
}
