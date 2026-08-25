"""Render front/back inspection previews for an unrigged GLB."""

import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector


def args():
    values = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    if len(values) != 2:
        raise SystemExit("Expected: input.glb output-prefix")
    return Path(values[0]).resolve(), Path(values[1]).resolve()


def point_at(obj, target):
    obj.rotation_euler = (target - obj.location).to_track_quat("-Z", "Y").to_euler()


def main():
    source, output_prefix = args()
    output_prefix.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=str(source))

    meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    corners = [obj.matrix_world @ Vector(corner) for obj in meshes for corner in obj.bound_box]
    minimum = Vector((min(v.x for v in corners), min(v.y for v in corners), min(v.z for v in corners)))
    maximum = Vector((max(v.x for v in corners), max(v.y for v in corners), max(v.z for v in corners)))
    center = (minimum + maximum) * 0.5
    height = maximum.z - minimum.z

    bpy.ops.object.light_add(type="AREA", location=(2.4, -2.8, 3.3))
    bpy.context.object.data.energy = 1000
    bpy.context.object.data.shape = "DISK"
    bpy.context.object.data.size = 3.0
    point_at(bpy.context.object, center)

    bpy.ops.object.light_add(type="AREA", location=(-2.5, 2.0, 2.0))
    bpy.context.object.data.energy = 500
    bpy.context.object.data.size = 2.5
    point_at(bpy.context.object, center)

    bpy.ops.object.camera_add()
    camera = bpy.context.object
    camera.data.type = "ORTHO"
    camera.data.ortho_scale = height * 1.12
    bpy.context.scene.camera = camera

    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.render.resolution_x = 640
    scene.render.resolution_y = 960
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.film_transparent = False
    scene.world = scene.world or bpy.data.worlds.new("PreviewWorld")
    scene.world.color = (0.04, 0.04, 0.04)
    scene.view_settings.look = "AgX - Medium High Contrast"

    distance = height * 2.4
    for suffix, direction in (("minus_y", -1), ("plus_y", 1)):
        camera.location = center + Vector((0.0, direction * distance, height * 0.02))
        point_at(camera, center)
        scene.render.filepath = str(output_prefix) + f"-{suffix}.png"
        bpy.ops.render.render(write_still=True)


if __name__ == "__main__":
    main()
