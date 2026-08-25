"""Print skinning relationships after re-importing a generated FBX."""

import json
import sys

import bpy


path = sys.argv[sys.argv.index("--") + 1]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=path)
report = []
for obj in bpy.context.scene.objects:
    if obj.type != "MESH":
        continue
    memberships = sum(len(vertex.groups) for vertex in obj.data.vertices)
    report.append({
        "mesh": obj.name,
        "vertices": len(obj.data.vertices),
        "vertex_groups": [group.name for group in obj.vertex_groups],
        "memberships": memberships,
        "parent": obj.parent.name if obj.parent else None,
        "modifiers": [
            {"name": modifier.name, "type": modifier.type, "object": getattr(modifier, "object", None).name if getattr(modifier, "object", None) else None}
            for modifier in obj.modifiers
        ],
    })
print("SPEAKUPXR_FBX_SKIN=" + json.dumps(report, ensure_ascii=False))
