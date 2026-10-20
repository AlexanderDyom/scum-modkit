"""Turn a mod's Python staging scripts (built on Chest_Mod/tools/rename_names.py) into modkit.json "edits".

Usage: python record_python_edits.py <mod tools dir> <reference stage> <game_es dir> <script.py>... [--texts <module>]
  The scripts run as usual, but the rename_names functions they call and their file copies are recorded instead of done
  (the reference stage is only read: export names). Copies from <game_es dir> (game files unpacked into the mod) become
  "extract"/"copyPackage" edits that take the package from the game. --texts <module> adds a "localize" edit from a
  module with texts(lang), loc_key(package, prop), NAMESPACE, SOURCE_LANG, CULTURES (Chest_Mod/tools/chestmod_texts.py).
Prints {"edits": [...]} as UTF-8 JSON.
"""
import contextlib
import json
import os
import runpy
import shutil
import sys

args = sys.argv[1:]
texts_module = None
if "--texts" in args:
    i = args.index("--texts")
    texts_module = args[i + 1]
    del args[i:i + 2]
tools, stage, game_es, scripts = os.path.abspath(args[0]), os.path.abspath(args[1]), os.path.abspath(args[2]), args[3:]
sys.path.insert(0, tools)
import rename_names  # noqa: E402

edits = []


def package(path):
    """Staged or game_es file path -> /Game package path."""
    p = os.path.abspath(path).replace("\\", "/")
    for root, prefix in ((stage.replace("\\", "/") + "/SCUM/Content/", "/Game/"), (game_es.replace("\\", "/") + "/", "/Game/ConZ_Files/")):
        if p.startswith(root):
            return prefix + os.path.splitext(p[len(root):])[0]
    raise ValueError(f"not in the stage or game_es: {path}")


def rename(path, pairs, subs=(), add=(), optional=False):
    edits.append({"op": "rename", "package": package(path), "exact": dict(pairs), "subs": [list(s) for s in subs],
                  "add": list(add), "optional": bool(optional)})
    return 1


def add_ingredient_slot(uasset, recipe_export, ci_pkg, ci_name, amount):
    edits.append({"op": "addIngredientSlot", "package": package(uasset), "export": recipe_export, "tagPackage": ci_pkg,
                  "tag": ci_name, "amount": amount})


def set_slot_amount(uasset, recipe_export, slot_index, amount):
    edits.append({"op": "setSlotAmount", "package": package(uasset), "export": recipe_export, "slot": slot_index,
                  "amount": amount})


def add_ingredient_tag(uasset, ci_package, ci_name, amount=1.0):
    edits.append({"op": "addIngredientTag", "package": package(uasset), "tagPackage": ci_package, "tag": ci_name,
                  "amount": amount})


def edit_export(uasset, export_name, fn):
    if fn.__name__ == "_null_skin":       # patch_staging.py: SkinRecipe = None
        edits.append({"op": "setObject", "package": package(uasset), "export": export_name, "property": "SkinRecipe",
                      "value": 0})
        return
    raise NotImplementedError(f"edit_export with {fn.__name__}: no modkit edit for it")


def copyfile(src, dst):
    src_pkg, dst_pkg = package(src), package(dst)
    op = {"op": "extract", "package": src_pkg} if src_pkg == dst_pkg else {"op": "copyPackage", "from": src_pkg, "to": dst_pkg}
    if not edits or edits[-1] != op:      # .uasset and .uexp: one edit
        edits.append(op)
    return dst


for name, fn in (("rename", rename), ("add_ingredient_slot", add_ingredient_slot), ("set_slot_amount", set_slot_amount),
                 ("add_ingredient_tag", add_ingredient_tag), ("edit_export", edit_export)):
    setattr(rename_names, name, fn)
shutil.copyfile = copyfile

for script in scripts:
    sys.argv = [script, stage]
    with contextlib.redirect_stdout(sys.stderr):      # the scripts' own messages
        runpy.run_path(script, run_name="__main__")

if texts_module:
    t = __import__(texts_module)
    src = t.texts(t.SOURCE_LANG)
    tr = {folder: t.texts(lang) for folder, lang in t.CULTURES.items()}
    edits.append({"op": "localize", "namespace": t.NAMESPACE, "entries": [
        {"package": pkg, "export": export, "property": prop, "key": t.loc_key(pkg, prop), "source": value,
         "translations": {folder: tr[folder][(pkg, export, prop)] for folder in t.CULTURES}}
        for (pkg, export, prop), value in src.items()]})

sys.stdout.reconfigure(encoding="utf-8")
json.dump({"edits": edits}, sys.stdout, ensure_ascii=False, indent=1)
