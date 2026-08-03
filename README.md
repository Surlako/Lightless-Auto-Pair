# Lightless Auto Pair

A Dalamud companion plugin for **Lightless Sync** that automatically sends pairing requests to nearby users who are actively broadcasting through **Lightfinder**.

## Implemented safeguards

- Master toggle, off by default.
- Configurable delay from **3 to 30 seconds** between requests.
- Persistent lifetime blacklist for identified declined requests.
- Clear-all and per-person blacklist removal controls.
- Never contacts people already paired.
- Never contacts people with a Lightless-reported pending request.
- Never repeats a request already tracked as outgoing and pending by this plugin, including after a restart.
- Status log with contacts, accepted pairs, declines, pauses, and errors.
- Automatically pauses while Lightless is disconnected and resumes when it reconnects if the master toggle is still enabled.

Open the window with:

```text
/lap
```

The previous `/lightautopair` command remains available as a legacy alias.

## Compatibility target

The reflection bridge targets **Lightless Sync 3.2.3.0** on **Dalamud API 15**. Version 0.1.1.0 adds exact runtime-root discovery and active service-scope resolution for the current Lightless host layout. Lightless currently does not expose Lightfinder pairing over public IPC, so this plugin accesses its internal services by reflection. A Lightless update can require a compatibility update here.

## Decline detection

The plugin subscribes to Lightless's internal notification mediator and looks for pair-request decline notifications. It matches the notification to an outgoing pending request using the hashed CID, display name, character name, world, or profile text. When exactly one outgoing request is pending and Lightless emits a generic decline notification, that request is treated as the unambiguous match.

If Lightless changes or suppresses its decline notification, the plugin will not guess when multiple requests are pending. It records an identification error in the status log instead of blacklisting the wrong person.

## GitHub build and installation

1. Create a public GitHub repository, for example `LightlessAutoPair`.
2. Upload every file and folder from this repository package to the repository root.
3. Keep `.github/workflows/build-release.yml` in that exact path.
4. Open **Actions** and run **Build and publish plugin**.
5. When it succeeds, add this URL to `/xlsettings` → **Experimental** → **Custom Plugin Repositories**:

```text
https://raw.githubusercontent.com/YOUR_GITHUB_USERNAME/LightlessAutoPair/main/pluginmaster.json
```

6. Open `/xlplugins`, search for **Lightless Auto Pair**, and install it.

## Publishing an update

Increase `<Version>` in `LightlessAutoPair.csproj`, commit the change, and run the workflow again. A version can only be published once.

## Testing status

This is an initial compatibility build. It must be compiled through the included GitHub workflow and tested in game. Keep the master toggle off until the status panel confirms that Lightless is loaded and connected.

## License

AGPL-3.0-or-later.
