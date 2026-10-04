# Architecture invariants — Mod Settings Tool (IL2CPP repo)

Distilled guardrails (full manifest: local `CLAUDE.md` + sibling ModSettingsTool).

- `BasePlugin` / `Load()`; GUID `com.leptoon.modsettingstool` frozen once released.
- Read-only toward other mods; edit only via live `ConfigEntry.BoxedValue`.
- Never commit game/BepInEx binaries, `local.props`, or `.gamedump/`.
- No interactive IMGUI; fail-soft Harmony patches.
