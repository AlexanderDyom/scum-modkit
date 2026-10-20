"""Print the "cooking" section of modkit.json for a recipe_edit.py config module (e.g. Food_Mod/tools/food_config.py).

Usage: python cooking_config_to_json.py <path/to/food_config.py>
Tuples become JSON arrays, the same ops ScumModKit's CookingEdits.ParseOp reads.
"""
import importlib.util
import json
import sys

spec = importlib.util.spec_from_file_location("cfg", sys.argv[1])
cfg = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cfg)

out = {
    "recipes": cfg.RECIPES,
    "itemTexts": getattr(cfg, "ITEM_TEXTS", {}),
    "newRecipes": getattr(cfg, "NEW_RECIPES", []),
    "books": getattr(cfg, "BOOKS", {}),
    "ownBooks": getattr(cfg, "OWN_BOOKS", {}),
    "knownByDefault": getattr(cfg, "KNOWN_BY_DEFAULT", []),
}
sys.stdout.reconfigure(encoding="utf-8")
json.dump({"cooking": out}, sys.stdout, ensure_ascii=False, indent=2)
