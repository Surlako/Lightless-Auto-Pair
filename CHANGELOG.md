# Changelog

## 0.1.4.0

- Makes disabled, closed-window operation genuinely idle with a five-second health check.
- Skips nearby-player reflection scans while disabled unless status or pending-request tracking needs them.
- Avoids a duplicate bridge refresh during each nearby-player scan.
- Caches reflection metadata through unload-safe weak type caches.
- Uses immutable versioned release links for cache-safe updates.

## 0.1.3.0

- Added the shorter `/lap` command as the primary settings command.
- Kept `/lightautopair` as a backwards-compatible legacy alias.

## 0.1.2.0

- Added explicit protection for users already connected through a joined/shared syncshell.
- Added detection of nearby syncshell broadcasts marked `AlreadyJoined` by Lightless.
- Added fallback inspection of Lightless pair/group connection state.
- Added a status counter showing how many nearby users were ignored because of syncshell coverage.

## 0.1.1.0

- Fixed Lightless Sync 3.2.3.0 runtime discovery when Dalamud exposes a non-root Lightless object first.
- Added exact PluginLifecycle and LightlessPlugin resolution.
- Added direct resolution through the active runtime service scope and host provider.
- Added fallback object-graph service discovery and clearer bridge diagnostics.
- Split Lightless detection from internal bridge readiness in the status window.

## 0.1.0.0

- Initial Lightless Sync 3.2.3.0 compatibility prototype.
- Added master toggle, 3–30 second delay, permanent decline blacklist, pending and paired protection, status log, and disconnect auto-pause.
- Added Lightless notification subscription for decline detection.
