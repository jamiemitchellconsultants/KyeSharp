# Kye Level Files

This folder contains level data used by the game and editor.

## Single level file format

```json
{
  "id": "my-level",
  "name": "My Level",
  "hint": "Optional hint",
  "map": [
    "####################",
    "#..................#",
    "#..................#",
    "#..................#",
    "#..................#",
    "#..................#",
    "#..................#",
    "#..................#",
    "#..................#",
    "#..................#",
    "#..................#",
    "#..................#",
    "#..................#",
    "#.......P......E...#",
    "####################"
  ]
}
```

Map rules:
- Exactly 15 rows
- Exactly 20 characters per row
- Exactly one `P` and one `E`
- Allowed symbols: `# . * E P r b v s t a w L R U D`

`t` is a clockwise turner tile that rotates roundel/rocky travel direction by 90 degrees clockwise when they enter it.

`a` is an anti-clockwise turner tile that rotates roundel/rocky travel direction by 90 degrees anti-clockwise when they enter it.

`w` is a worm hole tile. Levels must contain either 0 or 2 worm holes. When the player enters one worm hole, they exit the other.

`L`, `R`, `U`, `D` are directional pushers (left/right/up/down). A pusher rotates when it enters turners (`t` clockwise, `a` anti-clockwise), pushes movable tiles one cell forward if space is available, and reverses direction when blocked by a tile that cannot move.

## Manifest

`index.json` lists built-in levels and metadata such as pack and tier labels.

## Custom packs

Use `example-pack.json` as a template for imported packs.

