# HD2 Helper Auto Reload 0.3.0-test

Bingus Shared Loader / Arsenal additive addon. It does not replace the game's
boot script, the shared loader, or an installed HD2 Helper executable.

## Reload Triggers

- Usable ammunition in the held weapon changes from a positive count to zero.
- The actual held weapon changes to an empty weapon.
- A new fire-key press attempts to fire an empty weapon.

Requests require known zero ammo, a known positive reserve, a known idle reload
state, an unambiguous local held weapon, and player movement/rotation control.
The addon checks at most every 20 ms, plus game-frame scheduling. A 40 ms
reload-key pulse is sent through Windows SendInput. It does not write ammunition
or alter game state. An initial empty reading alone does not trigger a reload.
An event can wait up to 350 ms for complete data, with a 350 ms repeat guard.

Heat/laser weapons, underbarrels, throwables, melee, vehicle and mounted weapons
are excluded in this first test. Unknown data never counts as empty. The reader
is derived from HD2 HUD+ 0.1.2 with credit and its packaged reuse permission.
Its compatibility with the current game needs a live mission test. Schema checks
and Lua errors disable action instead of guessing. This does not certify that
mod use is accepted by the game or its anti-cheat.

## Installation

1. Close the game before deploying mods.
2. Import the release ZIP into Arsenal.
3. Enable this addon and Bingus Shared Loader v15+ / API 1, then deploy.
4. Disable the helper's existing screen-based automatic reload and the old Auto
   Reload diagnostic/helper scripts while testing to prevent double input.
5. Launch the game normally and test all three triggers in a mission.

HD2 HUD+ does not need to be installed/enabled. The read-only modules required
by this addon are embedded, but none of its display or startup code is included.

Default keys are left mouse for fire, R for reload, and F8 for pause/resume.
On first startup the addon creates `%APPDATA%\HD2AutoReload.ini` when possible:

```ini
enabled=true
fire_vk=1
reload_vk=82
pause_vk=119
```

These are Windows virtual-key numbers; restart the game after editing. Mouse
reload bindings are not supported. Enter tracks chat opening/sending; Escape
clears that chat guard. Holding Enter, Escape, or Tab blocks requests. Control
fields additionally gate menus and other non-player-control states, but their
actual game behavior must be tested. F8 is an emergency pause/resume toggle.

Diagnostics are written through the Bingus loader to
`%LOCALAPPDATA%\CowboyBingus\Helldivers2\Logs\hd2_helper_auto_reload.log`.
Look for START, ready, RELOAD followed by
one of ammo-exhausted / weapon-swapped / fire-attempt, or a blocking reason.
RELOAD means an input was accepted by Windows, not confirmed completion by the
game. INPUT_FAILED or DISABLED means no further guessed action is taken.

## Build And Test

Requires Node.js, PowerShell, and the game's LuaJIT `bin/lua51.dll`. Build reads
the licensed HD2 HUD+ 0.1.2 package, verifies its SHA-256, and extracts only the
non-rendering source modules. Tests run Lua in a separate process with mocked
game/input APIs, never attach to the game or send actual inputs.

```powershell
node build.cjs
./test.ps1 -LuaDll '<Helldivers 2 folder>/bin/lua51.dll'
Compress-Archive -Path './dist/HD2-AutoReload-0.3.0-test/*' -DestinationPath './dist/HD2-AutoReload-0.3.0-test.zip'
```

The credited reader sources and original permission README are in `vendor/`.
To re-extract the pinned installed package, pass its folder to `build.cjs`.
