"""Inspect a GLB with the repository's installed Blender runtime.

Usage:
  blender --background --python inspect_character.py -- input.glb report.json
"""

import json
import sys
from pathlib import Path

import bpy
from mathutils import Vector


def argv_after_separator():
    return sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []


def world_corners(obj):
    return [obj.matrix_world @ Vector(corner) for corner in obj.bound_box]


def main():
    args = argv_after_separator()
    if len(args) != 2:
        raise SystemExit("Expected: input.glb report.json")

    source = Path(args[0]).resolve()
    report_path = Path(args[1]).resolve()
    report_path.parent.mkdir(parents=True, exist_ok=True)

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=str(source))

    mesh_objects = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    armatures = [obj for obj in bpy.context.scene.objects if obj.type == "ARMATURE"]
    corners = [corner for obj in mesh_objects for corner in world_corners(obj)]
    if not corners:
        raise RuntimeError(f"No mesh objects found in {source}")

    minimum = Vector((min(v.x for v in corners), min(v.y for v in corners), min(v.z for v in corners)))
    maximum = Vector((max(v.x for v in corners), max(v.y for v in corners), max(v.z for v in corners)))
    dimensions = maximum - minimum

    objects = []
    total_triangles = 0
    for obj in mesh_objects:
        triangles = sum(len(poly.vertices) - 2 for poly in obj.data.polygons)
        total_triangles += triangles
        objects.append(
            {
                "name": obj.name,
                "vertices": len(obj.data.vertices),
                "triangles": triangles,
                "dimensions": [round(v, 6) for v in obj.dimensions],
                "location": [round(v, 6) for v in obj.location],
                "materials": [slot.material.name if slot.material else None for slot in obj.material_slots],
            }
        )

    report = {
        "source": str(source),
        "mesh_count": len(mesh_objects),
        "armature_count": len(armatures),
        "triangles": total_triangles,
        "bounds_min": [round(v, 6) for v in minimum],
        "bounds_max": [round(v, 6) for v in maximum],
        "dimensions": [round(v, 6) for v in dimensions],
        "objects": objects,
    }
    report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print("SPEAKUPXR_INSPECTION=" + json.dumps(report, ensure_ascii=False))


if __name__ == "__main__":
    main()
