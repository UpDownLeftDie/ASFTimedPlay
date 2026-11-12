### Changelog

This is automated GitHub deployment, human-readable changelog should be available soon.

### ASF compatibility (dual zip)

This release may include versioned plugin assets so ASF picks the right build:

- `ASFTimedPlay-V6.zip` — ArchiSteamFarm **6.x** (stable)
- `ASFTimedPlay-V7.zip` — ArchiSteamFarm **7.x**

ASF matches `{PluginName}-V{Major}.zip` to its own major version. Keep both on the same release while ASF 6 and 7 coexist.

**Release checklist**

1. Tag from `main` (ASF 6 line) → creates the GitHub release with `ASFTimedPlay-V6.zip`.
2. On branch `compat/asf-v7`, run workflow **Plugin-publish** → **Run workflow** → set `attach_to_release` to that tag → uploads `ASFTimedPlay-V7.zip` onto the same release.
