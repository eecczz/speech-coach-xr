# Avatar assets

Drop VRM files here. The practice page loads `/avatars/default.vrm` by default.

## Quick sample

For initial testing you can download any free VRM from:
- VRoid Hub: https://hub.vroid.com/en/ (filter: "free use", check redistribution terms)
- pixiv/three-vrm samples: https://github.com/pixiv/three-vrm/tree/dev/packages/three-vrm/examples/models

Rename your chosen file to `default.vrm` and place it in this directory.

If no VRM is present, the practice page falls back to a primitive head/body
mesh that still reflects MediaPipe tracking (so you can verify the rest of
the pipeline before sourcing a real VRM).

## Avatar style swap

Additional styles map to filenames:
- `?style=default` → `default.vrm`
- `?style=date-suit` → `date-suit.vrm`

Add new entries in `src/avatar/registry.ts`.
