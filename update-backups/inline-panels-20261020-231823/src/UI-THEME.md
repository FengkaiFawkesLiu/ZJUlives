# Shared UI conventions

- Use `Theme.Card`, `Theme.Button`, `Theme.Field`, `Theme.Combo` for new modules.
- Use `GlassTheme.Surface` / dynamic resource `Glass.Surface` for neutral panels; use SetResourceReference for live changes; Theme.Card already binds this resource.
- Use `GlassTheme.Edge` for thin glass boundaries. Never add chromatic overlays over text.
- Use `Glass.Reading` for dense editor surfaces and `Glass.Input` for controls. Keep text fully opaque.
- `GlassTheme.Backdrop` and `GlassTheme.Sidebar` define the app shell; do not add opaque page backgrounds.
- Reuse `Motion.DurationMs` (500 ms) and respect `Motion.Enabled` for transitions.
- New modules enter the existing body and navigation hierarchy, not a new window. Existing editing dialogs remain supported.
- Glass is rendered inside the application; it does not capture the desktop or request network access.
- Settings.GlassOpacity is local, defaults to 0.06, and is clamped on load.
