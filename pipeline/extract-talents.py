#!/usr/bin/env python
"""Extract Shadow Dungeon's talent-tree layout + skill icons.

Shadow Dungeon (Unity 2019.4.40f1, Mono) builds its talent panel from scene
objects in level1: the TalentManager MonoBehaviour (scene singleton) carries

  * XiCAV   : CanvasGroup[12]  -- one "Tree (N)" container GameObject per Xi
              (Xi = subclass id 0..11, three per PlayerType archetype)
  * DFXiCAV : CanvasGroup      -- the shared "Tree DF" (Divine Favor /
              "Paragon Talents") container, unlocked at level 100
  * iconDT  : IconData[12]     -- per-Xi sprite arrays ("Icon SkillA NN");
              a skill row's `icon` column indexes iconDT[Xi].icon[i]
              (iconDTB holds the greyscale locked variants, not exported)
  * SPCA/SPCB : IconData       -- Divine-Favor icon sheets (colored/locked);
              DF choice rows index SPCA.icon[Icon]
  * skillTA : TextAsset[10]    -- the CSV tables (0 SampleF, 1 SampleS,
              2 CompF, 3 CompS, 4 DotF, 5 DotS, 6 Bei, 7 DF, 8/9 change)
  * XiTA    : TextAsset        -- Xi table (tree names, 13 rows incl. DF)

Every tree node is a SkillBT MonoBehaviour (fields IndexName / Xi /
SkillType) sitting on a "Bottom" GameObject under a "SkillBT (n)" group
inside its Tree container; Divine Favor uses SKillBT_DF (field Index) and
SKillBT_Lie (per-element column counters, field Type). Node positions are
the RectTransform local positions accumulated up to the tree container --
i.e. exact Unity scene coordinates.

Prerequisite links are not scene line objects but data: son tables carry
FrontSkill/FrontSkillType/FatherSkill columns (TalentManager.SetSkillBT
uses FrontSkill to decide Unlock), and the DF table carries FA/FB/FC parent
node indices. Links are emitted from those columns.

Archetype grouping (PlayerType -> 3 Xi) is hardcoded in TalentManager:
SetStart() shows XiCAV[PLType*3 .. PLType*3+2] and
GetAvailableShortcutTalentPages() iterates `PL.PLType * 3` .. `+3`;
AddSkillFW() maps `skill.Xi / 3` -> character, `skill.Xi % 3` -> tree.
Archetype display names come from the Start_FY localization TextAsset
(player_type0..3 = Mage / Paladin / Ranger / Necromancer).

Outputs:
  web/public/data/talent-trees.json
  web/public/icons/skills/xi{NN}_{i}.png   (tree icons, per-Xi sheet index)
  web/public/icons/skills/df_{i}.png       (Divine Favor icons, SPCA index)

Game files are opened read-only; nothing in the game folder is modified.
"""

from __future__ import annotations

import json
import os
import pathlib
import struct
import sys
import time

import UnityPy
from PIL import Image
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

GAME_DATA = r"F:\SteamLibrary\steamapps\common\Shadow Dungeon\Shadow Dungeon_Data"
GAME_ROOT = os.path.dirname(GAME_DATA)
REPO = pathlib.Path(__file__).resolve().parent.parent
OUT_ICONS = REPO / "web" / "public" / "icons" / "skills"
OUT_JSON = REPO / "web" / "public" / "data" / "talent-trees.json"
SKILLS_JSON = REPO / "web" / "public" / "data" / "skills.json"
UNITY_VERSION = "2019.4.40f1"
NONE = 10000  # SkilDFData.NoneValue

ASSET_FILES = [
    "level1",
    "sharedassets0.assets",
    "sharedassets1.assets",
    "sharedassets2.assets",
    "resources.assets",
    "globalgamemanagers.assets",
]

# skillTA table index -> (src tag, is_son_table)
SKILL_TABLES = {
    0: ("sampleF", False),
    1: ("sampleS", True),
    2: ("compF", False),
    3: ("compS", True),
    4: ("dotF", False),
    5: ("dotS", True),
    6: ("bei", False),
}


def load_environment() -> UnityPy.Environment:
    paths = [os.path.join(GAME_DATA, f) for f in ASSET_FILES]
    for p in paths:
        if not os.path.isfile(p):
            sys.exit(f"missing game file: {p}")
    env = UnityPy.load(*paths)
    gen = TypeTreeGenerator(UNITY_VERSION)
    gen.load_local_game(GAME_ROOT)  # Managed/*.dll -> MonoBehaviour typetrees
    env.typetree_generator = gen
    return env


# ---------------------------------------------------------------- scripts --
def collect_behaviours(env, class_names):
    """Map class name -> list of MonoBehaviour ObjectReaders (cheap raw scan)."""
    keys = {}
    for obj in env.objects:
        if obj.type.name == "MonoScript":
            d = obj.read()
            if d.m_ClassName in class_names:
                keys[(obj.assets_file.name.lower(), obj.path_id)] = d.m_ClassName
    if not keys:
        sys.exit(f"no MonoScripts found for {class_names}")
    out = {c: [] for c in class_names}
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        raw = obj.get_raw_data()
        if len(raw) < 28:
            continue
        file_id = struct.unpack_from("<i", raw, 16)[0]
        path_id = struct.unpack_from("<q", raw, 20)[0]
        af = obj.assets_file
        if file_id == 0:
            script_file = af.name.lower()
        else:
            script_file = os.path.basename(str(af.externals[file_id - 1].path)).lower()
        cls = keys.get((script_file, path_id))
        if cls is not None:
            out[cls].append(obj)
    return out


# ------------------------------------------------------------- transforms --
def get_transform(go):
    for c in go.m_Component:
        comp = c.component.deref()
        if comp.type.name in ("RectTransform", "Transform"):
            return comp.read()
    return None


def position_in_tree(mb, roots):
    """Accumulate RectTransform local positions from the MB's GameObject up
    to a known tree-container GameObject. Returns (tree_key, x, y) with y-up
    Unity scene coordinates relative to the container, or (None, 0, 0)."""
    go = mb.m_GameObject.deref().read()
    tr = get_transform(go)
    x = y = 0.0
    sx = sy = 1.0
    for _ in range(40):
        g = tr.m_GameObject.deref().read()
        key = roots.get(g.object_reader.path_id)
        if key is not None:
            return key, x, y
        lp = tr.m_LocalPosition
        ls = tr.m_LocalScale
        x = x * ls.x + lp.x
        y = y * ls.y + lp.y
        f = tr.m_Father
        if f.m_PathID == 0:
            break
        tr = f.deref().read()
    return None, x, y


# -------------------------------------------------------------------- csv --
def read_text_asset(pptr) -> str:
    d = pptr.deref().read()
    s = d.m_Script
    if isinstance(s, bytes):
        s = s.decode("utf-8", errors="replace")
    return s


def csv_rows(text: str):
    """Mimic TalentManager.LoadTextFile: split('\\n') then split(',').
    Cells are stripped (C# int.Parse tolerates the trailing \\r)."""
    return [[c.strip() for c in line.split(",")] for line in text.split("\n")]


def parse_skill_tables(tm):
    """Parse skill CSVs 0..6. Returns (skills, links).
    skills: list of dicts {name, src, xi, icon, price, unlock, max}
    links:  list of (xi, front, son) from son tables."""
    skills, links = [], []
    for ti, (src, is_son) in SKILL_TABLES.items():
        rows = csv_rows(read_text_asset(tm.skillTA[ti]))
        # game iterates rows 1 .. len-2 inclusive
        for r in rows[1:-1]:
            if len(r) < 8 or not r[1]:
                continue
            skills.append(
                {
                    "name": r[1],
                    "src": src,
                    "icon": int(r[2]),
                    "price": int(r[3]),
                    "unlock": int(r[4]),
                    "xi": int(r[5]),
                    "max": int(r[6]),
                }
            )
            if is_son:
                # cols: 8 FrontSkill, 9 FrontSkillType, 10 FatherSkill
                links.append((int(r[5]), r[8], r[1]))
    return skills, links


def parse_df_table(tm):
    """Parse skillTA[7] (Divine Favor). Column layout per LoadData_DF:
    1 Index, 2 SK_Count, 3 Unlock_Point, 4 Level_Max, 5-7 LieA/B/C,
    8-10 FatherA/B/C, then 3 x (IndexName, Info, Icon, Type, Number)."""
    rows = csv_rows(read_text_asset(tm.skillTA[7]))
    out = []
    for r in rows[1:]:
        if len(r) < 26 or not r[1] or not r[1].strip("-").isdigit():
            continue
        lits = []
        c = 11
        for _ in range(3):
            name, info, icon, typ, num = r[c], r[c + 1], int(r[c + 2]), int(r[c + 3]), int(r[c + 4])
            c += 5
            # SkilDFData.IsValidLit
            if name and name != str(NONE) and icon != NONE and typ != NONE and num != NONE:
                lits.append({"name": name, "info": info, "icon": icon})
        out.append(
            {
                "index": int(r[1]),
                "sk_count": int(r[2]),
                "unlock": int(r[3]),
                "max": int(r[4]),
                "lie": [v for v in (int(r[5]), int(r[6]), int(r[7])) if v != NONE],
                "fathers": [v for v in (int(r[8]), int(r[9]), int(r[10])) if v != NONE],
                "choices": lits,
            }
        )
    return out


def parse_tree_names(tm):
    """XiTA rows: row j (1-based) -> XiData[j-1].IndexName = col 1."""
    rows = csv_rows(read_text_asset(tm.XiTA))
    names = {}
    for j, r in enumerate(rows[1:]):
        if len(r) >= 2 and r[1]:
            names[j] = r[1]
    return names


def load_fy(env, name):
    """A *_FY localization TextAsset as a dict (key -> per-language map)."""
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        d = obj.read()
        if d.m_Name != name:
            continue
        s = d.m_Script
        if isinstance(s, bytes):
            s = s.decode("utf-8-sig", errors="replace")
        try:
            return json.loads(s.lstrip("\ufeff"))
        except ValueError:
            break
    return {}


def archetype_names(env):
    """Start_FY localization TextAsset: player_type0..3, English column."""
    fallback = ["Mage", "Paladin", "Ranger", "Necromancer"]
    j = load_fy(env, "Start_FY")
    return [
        j.get(f"player_type{i}", {}).get("English", fallback[i]) for i in range(4)
    ]


# ------------------------------------------------------------------ icons --
def compose_icon(sprite) -> Image.Image:
    """Paste the tight-trimmed sprite back onto its full logical rect
    (same technique as extract-icons.py)."""
    img = sprite.image
    if img.mode != "RGBA":
        img = img.convert("RGBA")
    rect_w = round(sprite.m_Rect.width)
    rect_h = round(sprite.m_Rect.height)
    rd = sprite.m_RD
    packed = bool(rd.settingsRaw & 1)
    if packed or rect_w <= 0 or rect_h <= 0:
        return img
    ox = round(rd.textureRectOffset.x)
    oy = round(rd.textureRectOffset.y)
    if ox < 0 or oy < 0 or ox + img.width > rect_w or oy + img.height > rect_h:
        return img
    canvas = Image.new("RGBA", (rect_w, rect_h), (0, 0, 0, 0))
    canvas.paste(img, (ox, rect_h - oy - img.height))
    return canvas


class IconExporter:
    def __init__(self, out_dir: pathlib.Path):
        self.out_dir = out_dir
        self.sheets = {}  # sheet tag -> list of sprite PPtrs
        self.written = {}  # (tag, idx) -> rel path
        self.sizes = []

    def register_sheet(self, tag, icondata_pptr):
        icd = icondata_pptr.deref().read()
        self.sheets[tag] = list(icd.icon)

    def export(self, tag, idx):
        key = (tag, idx)
        if key in self.written:
            return self.written[key]
        arr = self.sheets.get(tag)
        if arr is None or idx < 0 or idx >= len(arr):
            return None
        pptr = arr[idx]
        if pptr is None or pptr.m_PathID == 0:
            return None
        sp = pptr.deref().read()
        img = compose_icon(sp)
        rel = f"icons/skills/{tag}_{idx}.png"
        abs_path = self.out_dir / f"{tag}_{idx}.png"
        abs_path.parent.mkdir(parents=True, exist_ok=True)
        img.save(abs_path, optimize=True)
        self.written[key] = rel
        self.sizes.append((img.width, img.height))
        return rel


# ------------------------------------------------------------------- main --
def main():
    t0 = time.time()
    env = load_environment()
    print(f"loaded assets in {time.time() - t0:.1f}s")

    found = collect_behaviours(
        env, {"TalentManager", "SkillBT", "SKillBT_DF", "SKillBT_Lie"}
    )
    if len(found["TalentManager"]) != 1:
        sys.exit(f"expected 1 TalentManager, got {len(found['TalentManager'])}")
    tm = found["TalentManager"][0].read()
    print(
        f"TalentManager in {found['TalentManager'][0].assets_file.name}; "
        f"SkillBT={len(found['SkillBT'])} SKillBT_DF={len(found['SKillBT_DF'])} "
        f"SKillBT_Lie={len(found['SKillBT_Lie'])}"
    )

    # ---- static data from the CSV TextAssets ----
    skills, son_links = parse_skill_tables(tm)
    df_rows = parse_df_table(tm)
    tree_names = parse_tree_names(tm)
    arch_names = archetype_names(env)
    skill_fy = load_fy(env, "Skill_FY")  # English labels for DF choice keys
    skills_by_name = {}
    for s in skills:
        if s["name"] in skills_by_name:
            print(f"WARNING duplicate skill IndexName {s['name']}")
        skills_by_name[s["name"]] = s
    print(
        f"CSV: {len(skills)} skills, {len(son_links)} prerequisite links, "
        f"{len(df_rows)} DF nodes, trees={len(tree_names)}, archetypes={arch_names}"
    )

    # ---- tree container roots (GameObject path_id -> tree id 0..12) ----
    roots = {}
    for i, p in enumerate(tm.XiCAV):
        cg = p.deref().read()
        go = cg.m_GameObject.deref().read()
        roots[go.object_reader.path_id] = i
    dfcg = tm.DFXiCAV.deref().read()
    dfgo = dfcg.m_GameObject.deref().read()
    roots[dfgo.object_reader.path_id] = 12

    # ---- scene node positions ----
    node_pos = {}  # (xi, IndexName) -> (x, y)  y-up, relative to tree root
    skipped = 0
    for o in found["SkillBT"]:
        mb = o.read()
        if not mb.IndexName:
            skipped += 1  # unused "Tree MB" template nodes (empty IndexName)
            continue
        tree, x, y = position_in_tree(mb, roots)
        if tree is None:
            print(f"WARNING {mb.IndexName}: not under any tree container")
            continue
        if tree != mb.Xi:
            print(f"WARNING {mb.IndexName}: Xi={mb.Xi} but under tree {tree}")
        key = (mb.Xi, mb.IndexName)
        if key in node_pos:
            print(f"WARNING duplicate scene node for {key}")
        node_pos[key] = (x, y)
    print(f"scene: {len(node_pos)} skill nodes ({skipped} empty template nodes skipped)")

    df_pos = {}  # DF Index -> (x, y)
    for o in found["SKillBT_DF"]:
        mb = o.read()
        tree, x, y = position_in_tree(mb, roots)
        if tree != 12:
            print(f"WARNING DF node {mb.Index} not under Tree DF")
        df_pos[mb.Index] = (x, y)

    lie_pos = {}  # column Type -> (x, y)
    for o in found["SKillBT_Lie"]:
        mb = o.read()
        tree, x, y = position_in_tree(mb, roots)
        if tree == 12:
            lie_pos[mb.Type] = (x, y)

    # ---- icons ----
    exp = IconExporter(OUT_ICONS)
    for xi, p in enumerate(tm.iconDT):
        exp.register_sheet(f"xi{xi:02d}", p)
    exp.register_sheet("df", tm.SPCA)

    # ---- assemble trees ----
    def normalizer(points):
        xs = [p[0] for p in points]
        ys = [p[1] for p in points]
        minx, maxy = min(xs), max(ys)
        return lambda x, y: (round(x - minx, 2), round(maxy - y, 2))

    trees = []
    missing_pos, missing_icon = [], []
    for xi in range(12):
        members = [s for s in skills if s["xi"] == xi]
        pts = [node_pos[(xi, s["name"])] for s in members if (xi, s["name"]) in node_pos]
        norm = normalizer(pts)
        nodes = []
        for s in members:
            pos = node_pos.get((xi, s["name"]))
            if pos is None:
                missing_pos.append(s["name"])
                continue
            icon = exp.export(f"xi{xi:02d}", s["icon"])
            if icon is None:
                missing_icon.append(s["name"])
            nx, ny = norm(*pos)
            nodes.append(
                {
                    "skill": s["name"],
                    "type": s["src"],
                    "x": nx,
                    "y": ny,
                    "rawX": round(pos[0], 2),
                    "rawY": round(pos[1], 2),
                    "icon": icon,
                    "max": s["max"],
                    "unlock": s["unlock"],
                }
            )
        nodes.sort(key=lambda n: (n["y"], n["x"]))
        names_in_tree = {n["skill"] for n in nodes}
        links = [
            [front, son]
            for (lxi, front, son) in son_links
            if lxi == xi and front in names_in_tree and son in names_in_tree
        ]
        trees.append(
            {
                "xi": xi,
                "name": tree_names.get(xi, f"Xi {xi}"),
                "nodes": nodes,
                "links": links,
            }
        )

    # Divine Favor tree (xi 12)
    pts = list(df_pos.values())
    norm = normalizer(pts)
    df_nodes = []
    for row in df_rows:
        pos = df_pos.get(row["index"])
        if pos is None:
            missing_pos.append(f"DF_{row['index']}")
            continue
        choices = []
        for lit in row["choices"]:
            icon = exp.export("df", lit["icon"])
            en = skill_fy.get(lit["name"], {}).get("English") or lit["name"]
            desc = skill_fy.get(lit["info"], {}).get("English")
            choice = {"name": lit["name"], "label": en, "info": lit["info"], "icon": icon}
            if desc:
                choice["desc"] = desc
            choices.append(choice)
        icon = choices[0]["icon"] if choices else None
        if icon is None:
            missing_icon.append(f"DF_{row['index']}")
        nx, ny = norm(*pos)
        df_nodes.append(
            {
                "skill": f"DF_{row['index']}",
                "type": "df",
                "x": nx,
                "y": ny,
                "rawX": round(pos[0], 2),
                "rawY": round(pos[1], 2),
                "icon": icon,
                "max": row["max"],
                "unlock": row["unlock"],
                "lie": row["lie"],
                "choices": choices,
            }
        )
    df_nodes.sort(key=lambda n: (n["y"], n["x"]))
    df_indices = {r["index"] for r in df_rows}
    df_links = [
        [f"DF_{f}", f"DF_{r['index']}"]
        for r in df_rows
        for f in r["fathers"]
        if f in df_indices
    ]
    df_columns = [
        {"lie": t, "x": norm(*p)[0], "y": norm(*p)[1]}
        for t, p in sorted(lie_pos.items())
    ]
    trees.append(
        {
            "xi": 12,
            "name": tree_names.get(12, "Paragon Talents"),
            "nodes": df_nodes,
            "links": df_links,
            "columns": df_columns,
        }
    )

    out = {
        "generatedBy": "pipeline/extract-talents.py",
        "source": (
            "Shadow Dungeon_Data/level1: TalentManager (XiCAV/DFXiCAV tree "
            "containers, skillTA CSV tables) + SkillBT/SKillBT_DF scene nodes; "
            "icons from iconDT[Xi]/SPCA IconData (sharedassets1.assets)"
        ),
        "synthesized": False,
        "coordinateSpace": (
            "x,y: pixels from the tree's top-left node (y grows downward); "
            "rawX,rawY: Unity scene-local position relative to the Tree (N) "
            "container GameObject (y grows upward)"
        ),
        "archetypes": [
            {"name": arch_names[i], "classIds": [i * 3, i * 3 + 1, i * 3 + 2]}
            for i in range(4)
        ],
        "trees": trees,
    }
    OUT_JSON.parent.mkdir(parents=True, exist_ok=True)
    with open(OUT_JSON, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=1)

    # ------------------------------------------------------------- verify --
    print("\n--- verification ---")
    node_count = sum(len(t["nodes"]) for t in trees)
    link_count = sum(len(t["links"]) for t in trees)
    print(f"trees: {len(trees)}, nodes: {node_count}, links: {link_count}")
    if missing_pos:
        print(f"MISSING POSITIONS ({len(missing_pos)}): {missing_pos[:10]}")
    if missing_icon:
        print(f"MISSING ICONS ({len(missing_icon)}): {missing_icon[:10]}")

    # every skills.json row appears in exactly one tree
    ok = True
    if SKILLS_JSON.is_file():
        sj = json.load(open(SKILLS_JSON, encoding="utf-8"))
        ci = {c: i for i, c in enumerate(sj["cols"])}
        placed = {}
        for t in trees:
            for n in t["nodes"]:
                placed.setdefault(n["skill"], []).append(t["xi"])
        miss = dup = bad = 0
        for r in sj["rows"]:
            name, xi, icon = r[ci["IndexName"]], r[ci["Xi"]], r[ci["icon"]]
            locs = placed.get(name, [])
            if len(locs) == 0:
                miss += 1
                print(f"  skills.json row NOT in any tree: {name}")
            elif len(locs) > 1 or locs[0] != xi:
                dup += 1
                print(f"  {name}: expected tree {xi}, found in {locs}")
            csv_skill = skills_by_name.get(name)
            if csv_skill is not None and icon is not None and csv_skill["icon"] != icon:
                bad += 1
                print(f"  {name}: icon index mismatch csv={csv_skill['icon']} skills.json={icon}")
        print(
            f"skills.json rows: {len(sj['rows'])}; missing from trees: {miss}, "
            f"misplaced/dup: {dup}, icon-index mismatches: {bad}"
        )
        ok = ok and miss == 0 and dup == 0 and bad == 0
    else:
        print("skills.json not found; skipped cross-check")

    # every node icon file exists
    no_file = 0
    for t in trees:
        for n in t["nodes"]:
            if not n["icon"] or not (REPO / "web" / "public" / n["icon"]).is_file():
                no_file += 1
                print(f"  node without icon file: {n['skill']} ({n['icon']})")
    print(f"nodes with missing icon files: {no_file}")
    ok = ok and no_file == 0 and not missing_pos

    if exp.sizes:
        ws = sorted(s[0] for s in exp.sizes)
        hs = sorted(s[1] for s in exp.sizes)
        print(
            f"exported {len(exp.written)} icons; size min {ws[0]}x{hs[0]}, "
            f"max {ws[-1]}x{hs[-1]}, median {ws[len(ws)//2]}x{hs[len(hs)//2]}"
        )
    print(f"wrote {OUT_JSON}")
    print(f"done in {time.time() - t0:.1f}s -- {'OK' if ok else 'WITH ISSUES'}")


if __name__ == "__main__":
    main()
