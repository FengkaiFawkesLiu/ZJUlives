# Shared UI conventions

- Use `Theme.Card`, `Theme.Button`, `Theme.Field`, `Theme.Combo` for new modules.
- Use `GlassTheme.Surface` / dynamic resource `Glass.Surface` for neutral panels; use SetResourceReference for live changes; Theme.Card already binds this resource.
- Use `GlassTheme.Edge` for thin glass boundaries. Never add chromatic overlays over text.
- Use `Glass.Reading` for dense editor surfaces and `Glass.Input` for controls. Keep text fully opaque.
- `GlassTheme.Backdrop` and `GlassTheme.Sidebar` define the app shell; do not add opaque page backgrounds.
- Reuse `Motion.DurationMs` (500 ms) and respect `Motion.Enabled` for transitions.
- New modules enter the existing body and navigation hierarchy, not a new window. Use Theme.Dialog (AppPanel) for editors and UiMessage for confirmations. These are embedded workspace columns, not Window instances.
- Glass is rendered inside the application; it does not capture the desktop or request network access.
- Settings.GlassOpacity is local, defaults to 0.06, and is clamped on load.
- Graph and mind-map surfaces use `GraphGlass.Panel` with transparent canvases and layered edge highlights. Color dispersion is a visual approximation, not an optical shader.
- Graph nodes use colored translucent glass only while held; release or lost capture restores the original fill. Physics stops after settling, saves once, and detaches on unload. Respect `Motion.Enabled`.
- Buttons provide keyboard focus in their rounded template; sliders use a round thumb and a highlighted track. Do not reintroduce rectangular default focus adorners in graph views.
- Knowledge-note editors set `AppPanel.FocusWorkspace`: the editor fills the workspace while the navigation stays fixed. Child tools overlay the right side and return to the same editor. Other modules retain their column layout.
- Note bodies use a paper surface; toolbar and edges use glass. Common actions stay compact; insertion, formatting, and relations expand on demand using `Motion.DurationMs`. Setting labels and values share one compact frame. Do not apply a default focus adorner to the title or body.

## Embedded panels

- Keep the 208px sidebar outside ColumnWorkspace. Never resize or translate the outer Window for a panel transition.
- Theme.Dialog returns an AppPanel; ShowDialog keeps existing synchronous save/confirmation sequencing using a local dispatcher frame. Closing is cancellable and Closed releases session resources.
- Child tools open another column; return closes only the top panel. Parent content remains visible. At limited widths/deeper stacks, rows share the same bounded workspace.
- Use Theme.Row for wrapping actions. Dense legacy editors use AdaptivePane to fit their logical width; scroll controls retain all content.
- Use RemarkAutosave for existing course/event/task remarks. It resolves the latest record by ID, debounces for 500ms, flushes on close, and restores the in-memory note on save failure. New unsaved records retain explicit Save.
- File pickers and external apps still use their system interfaces. UiMessage's native fallback is restricted to startup when no loaded main workspace exists.
