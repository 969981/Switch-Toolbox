#!/usr/bin/env python3
"""Independent SV Trinity -> glTF / skeleton / animation / material interoperability test.

This validates gftool output in temporary CI files, NOT native TRMDL editing in Switch-Toolbox.
No game assets are committed, uploaded, or published.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path
from urllib.parse import unquote, urlsplit


def validate_asset_refs(gltf: dict, basedir: Path):
    checked = {}
    for category in ("buffers", "images"):
        for item in gltf.get(category, []):
            uri = item.get("uri")
            if not uri or uri.startswith("data:"):
                continue
            if urlsplit(uri).scheme:
                raise ValueError("Remote asset reference unexpectedly present: " + uri)
            target = (basedir / unquote(uri)).resolve()
            if not target.is_relative_to(basedir.resolve()):
                raise ValueError("glTF path escapes export directory")
            if not target.is_file():
                raise FileNotFoundError(str(target))
            checked[uri] = hashlib.sha256(target.read_bytes()).hexdigest()
    return checked


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--gltf", type=Path, required=True)
    ap.add_argument("--require-animation", action="store_true")
    ap.add_argument("--min-lods", type=int, default=0)
    args = ap.parse_args()
    file = args.gltf.resolve()
    source = json.loads(file.read_text(encoding="utf-8-sig"))
    assert source.get("asset", {}).get("version", "").startswith("2."), "Not glTF 2"
    nodes, meshes, skins, materials = (source.get(k, []) for k in ("nodes", "meshes", "skins", "materials"))
    animations = source.get("animations", [])
    assert len(nodes) and len(meshes) and len(skins), "No geometry, nodes or skins"
    assert len(materials), "No materials exported"
    assert sum(len(m.get("primitives", [])) for m in meshes) > 0, "Empty mesh primitives"
    assert all(len(s.get("joints", [])) > 0 for s in skins), "Empty skeleton joints"
    if args.require_animation:
        assert animations and all(anim.get("channels") for anim in animations), "No animation channels"
    if args.min_lods:
        assert len(meshes) >= args.min_lods, "Too few LOD meshes"
    before = validate_asset_refs(source, file.parent)

    edited = copy.deepcopy(source)
    material = edited["materials"][0]
    pbr = material.setdefault("pbrMetallicRoughness", {})
    old_color = pbr.get("baseColorFactor", [1.0, 1.0, 1.0, 1.0])
    pbr["baseColorFactor"] = [0.375, 0.625, 0.875, float(old_color[3])]
    dest = file.with_name(file.stem + ".material-validated.gltf")
    dest.write_text(json.dumps(edited, ensure_ascii=False, indent=2), encoding="utf-8")
    reloaded = json.loads(dest.read_text(encoding="utf-8"))
    assert reloaded["materials"][0]["pbrMetallicRoughness"]["baseColorFactor"] == pbr["baseColorFactor"]
    assert reloaded["meshes"] == source["meshes"], "Geometry mutated by material metadata edit"
    assert reloaded.get("skins") == source.get("skins"), "Skeleton mutated"
    assert reloaded.get("animations") == source.get("animations"), "Animations mutated"
    assert validate_asset_refs(reloaded, file.parent) == before, "Texture / buffer assets mutated"
    print(json.dumps({
        "result": "PASS", "scope": "external SV gftool Trinity-to-glTF only",
        "model": file.stem, "meshes": len(meshes), "skins": len(skins),
        "bones": sum(len(s.get("joints", [])) for s in skins),
        "materials": len(materials), "animations": len(animations),
        "assets_checked": len(before),
        "material_edit_reloaded": True, "geometry_unchanged": True,
        "native_Trinity_import_roundtrip": False,
        "Switch_Toolbox_native_Trinity_edit": False
    }, ensure_ascii=False))


if __name__ == "__main__":
    main()
