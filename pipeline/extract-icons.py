#!/usr/bin/env python
"""Extract item / gem / consumable / set icon sprites from Shadow Dungeon.

Shadow Dungeon (Unity 2019.4.40f1, Mono) stores all inventory-item icons as
Sprites sliced from per-category sheet textures in sharedassets1.assets.
The game does NOT look icons up by name: the ItemManager MonoBehaviour
(scene singleton in level1) carries serialized arrays of IconData
ScriptableObjects (`public Sprite[] icon`), and CSV table rows select
`IconData[IconType].icon[Icon]` (weapons) or `IconBaoshi.icon[Icon]` (gems)
or `IconUse.icon[Icon]` (consumables).

This script re-implements that lookup offline:
  1. locate the ItemManager MonoScript, then its MonoBehaviour instance
  2. read its typetree (generated from the game's Managed DLLs)
  3. dereference every IconData -> Sprite -> crop from the sheet texture,
     composing the tight-trimmed pixels back onto the sprite's full logical
     rect (so all icons of a category share uniform dimensions and the
     in-game alignment inside their inventory cells)
  4. write PNGs to web/public/icons/<category>/<sprite-name>.png and an
     icons-index.json that preserves the exact array ORDER, because the
     game's (IconType, Icon) indices are positions in these arrays.

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
OUT_ROOT = str(pathlib.Path(__file__).resolve().parent.parent / "web" / "public" / "icons")
UNITY_VERSION = "2019.4.40f1"

# Files needed: level1 hosts the ItemManager scene object, sharedassets1
# hosts the IconData assets + sheet textures (.resS), globalgamemanagers
# hosts the MonoScripts. The others are loaded so PPtr externals resolve.
ASSET_FILES = [
    "level1",
    "sharedassets0.assets",
    "sharedassets1.assets",
    "sharedassets2.assets",
    "resources.assets",
    "globalgamemanagers.assets",
]

CATEGORY_WEAPONS = "weapons"
CATEGORY_GEMS = "gems"
CATEGORY_CONSUMABLES = "consumables"


def load_environment() -> UnityPy.Environment:
    paths = [os.path.join(GAME_DATA, f) for f in ASSET_FILES]
    for p in paths:
        if not os.path.isfile(p):
            sys.exit(f"missing game file: {p}")
    env = UnityPy.load(*paths)
    gen = TypeTreeGenerator(UNITY_VERSION)
    gen.load_local_game(GAME_ROOT)  # parses Managed/*.dll for MonoBehaviour typetrees
    env.typetree_generator = gen
    return env


def find_item_manager(env: UnityPy.Environment):
    """Locate the ItemManager MonoBehaviour without hardcoding path_ids."""
    # 1) all MonoScripts named ItemManager: (containing file basename, path_id)
    script_keys = set()
    for obj in env.objects:
        if obj.type.name == "MonoScript":
            d = obj.read()
            if d.m_ClassName == "ItemManager":
                script_keys.add((obj.assets_file.name.lower(), obj.path_id))
    if not script_keys:
        sys.exit("no ItemManager MonoScript found")

    # 2) scan MonoBehaviours cheaply via raw bytes:
    #    header = m_GameObject PPtr(12) | m_Enabled+pad(4) | m_Script PPtr(12)
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
            ext = af.externals[file_id - 1]
            script_file = os.path.basename(str(ext.path)).lower()
        if (script_file, path_id) in script_keys:
            mb = obj.read()
            if hasattr(mb, "IconData") and hasattr(mb, "IconBaoshi"):
                print(f"ItemManager MonoBehaviour: {af.name} path_id={obj.path_id}")
                return mb
    sys.exit("ItemManager MonoBehaviour not found in loaded files")


def compose_icon(sprite) -> Image.Image:
    """Return the sprite on its full logical rect (uniform per-category size).

    UnityPy's Sprite.image already handles textureRect cropping and packing
    rotation. The sheets here are tight-trimmed (settingsRaw=64, packed=0),
    so the decoded image is smaller than m_Rect; we paste it back at
    textureRectOffset (bottom-left origin) onto a transparent canvas of the
    full rect, restoring the game's in-cell alignment.
    """
    img = sprite.image
    if img.mode != "RGBA":
        img = img.convert("RGBA")
    rect_w = round(sprite.m_Rect.width)
    rect_h = round(sprite.m_Rect.height)
    rd = sprite.m_RD
    packed = bool(rd.settingsRaw & 1)
    if packed or rect_w <= 0 or rect_h <= 0:
        return img  # atlas-packed/rotated: trust UnityPy's unpacked image as-is
    ox = round(rd.textureRectOffset.x)
    oy = round(rd.textureRectOffset.y)
    if ox < 0 or oy < 0 or ox + img.width > rect_w or oy + img.height > rect_h:
        return img  # offset out of bounds (shouldn't happen) -> tight crop
    canvas = Image.new("RGBA", (rect_w, rect_h), (0, 0, 0, 0))
    canvas.paste(img, (ox, rect_h - oy - img.height))
    return canvas


class Exporter:
    def __init__(self, out_root: str):
        self.out_root = out_root
        self.sprites: dict[str, dict] = {}  # sprite name -> {path, w, h}
        self.by_path_id: dict[tuple, str] = {}  # (file, path_id) -> sprite name
        self.count = 0

    def export(self, pptr, category: str) -> str | None:
        """Export one sprite PPtr, return its (deduplicated) sprite name."""
        if pptr is None or pptr.m_PathID == 0:
            return None
        sp = pptr.deref().read()
        key = (sp.object_reader.assets_file.name, sp.object_reader.path_id)
        if key in self.by_path_id:
            return self.by_path_id[key]
        name = sp.m_Name
        if name in self.sprites:  # same name, different object: disambiguate
            name = f"{name}__{sp.object_reader.path_id}"
        img = compose_icon(sp)
        rel = f"icons/{category}/{name}.png"
        abs_path = os.path.join(self.out_root, category, f"{name}.png")
        os.makedirs(os.path.dirname(abs_path), exist_ok=True)
        img.save(abs_path, optimize=True)
        self.sprites[name] = {"path": rel, "w": img.width, "h": img.height}
        self.by_path_id[key] = name
        self.count += 1
        return name


def main():
    t0 = time.time()
    env = load_environment()
    print(f"loaded assets in {time.time() - t0:.1f}s")

    im = find_item_manager(env)
    exp = Exporter(OUT_ROOT)

    # --- weapons / armor / accessories / set pieces: IconData[IconType].icon[Icon]
    weapon_icon_types = []
    for icon_type, pptr in enumerate(im.IconData):
        icd = pptr.deref().read()
        names = [exp.export(sp, CATEGORY_WEAPONS) for sp in icd.icon]
        weapon_icon_types.append(
            {"iconType": icon_type, "sheet": icd.m_Name, "sprites": names}
        )
        print(f"IconType {icon_type:2d} {icd.m_Name:<8s} {len(names)} sprites")

    # --- gems (Baoshi): IconBaoshi.icon[Icon]
    icd = im.IconBaoshi.deref().read()
    gem_icons = {
        "sheet": icd.m_Name,
        "sprites": [exp.export(sp, CATEGORY_GEMS) for sp in icd.icon],
    }
    print(f"IconBaoshi   {icd.m_Name:<8s} {len(gem_icons['sprites'])} sprites")

    # --- consumables (UseItem): IconUse.icon[Icon]
    icd = im.IconUse.deref().read()
    use_icons = {
        "sheet": icd.m_Name,
        "sprites": [exp.export(sp, CATEGORY_CONSUMABLES) for sp in icd.icon],
    }
    print(f"IconUse      {icd.m_Name:<8s} {len(use_icons['sprites'])} sprites")

    # --- special gem-rune override icons (ItemIconUtil.GetBaoshiIcon UseType 3/4/5)
    special = {
        "skillRuneByElement": [exp.export(p, CATEGORY_GEMS) for p in im.SkillFW_Icon],
        "spcRune": exp.export(im.SPCFW_Icon, CATEGORY_GEMS),
        "baseRune": exp.export(im.BaseFW_Icon, CATEGORY_GEMS),
        "doubleIcons": [exp.export(p, CATEGORY_GEMS) for p in im.Double_Icon],
    }

    index = {
        "generatedBy": "pipeline/extract-icons.py",
        "source": (
            "Shadow Dungeon_Data: ItemManager MonoBehaviour (level1) -> IconData "
            "ScriptableObjects + sheet textures (sharedassets1.assets)"
        ),
        "cellSizePx": 60,  # 1 inventory grid cell == 60px; sprite rect = SizeX*60 x SizeY*60
        "sprites": exp.sprites,
        "weaponIconTypes": weapon_icon_types,
        "gemIcons": gem_icons,
        "useItemIcons": use_icons,
        "special": special,
    }
    os.makedirs(OUT_ROOT, exist_ok=True)
    with open(os.path.join(OUT_ROOT, "icons-index.json"), "w", encoding="utf-8") as f:
        json.dump(index, f, indent=1)

    print(f"\nexported {exp.count} unique sprites in {time.time() - t0:.1f}s")
    print(f"index: {os.path.join(OUT_ROOT, 'icons-index.json')}")


if __name__ == "__main__":
    main()
