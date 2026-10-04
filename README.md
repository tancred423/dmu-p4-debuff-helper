# DMU Helper

DMU Helper is a Dalamud plugin for P3 Black Hole assignments and P4 debuffs in Dancing Mad Ultimate.

It is DMU-only. The helper window pops into the relevant compact view: P3 Black Hole instructions when your Black Hole assignment is detected, or P4 debuff guidance when watched P4 statuses/tells are detected. Grand Cross and Chaos debuffs use the boss tell status for real/fake calls, while Flood debuffs show side guidance from the player's Wound pairing.

## Current Tracking

- P3 Black Hole: First/Second/Third in Line, Accretion, Primordial Crust, Earth Resistance Down II, and Black Hole hit records.
- Grand Cross: Compressed Water, Forked Lightning, Cursed Shriek, Acceleration Bomb.
- Chaos: Dynamic Fluid, Entropy.
- Flood: Black Wound, White Wound, Allagan Field, Beyond Death.

## Settings

- Enable or disable P3 and P4 tracking independently.
- Toggle Flood debuff tracking without disabling other P4 mechanics.
- Use game-name Black/White or visual-color Blue/Purple Flood destination calls.
- Optionally show `(Short)` or `(Long)` on Stack/Spread calls. A debuff applied at 55 seconds or less is Short; above 55 seconds is Long.

## Commands

Open settings:

```text
/dmu
/dmuh
/dmuhelper
```

## Dalamud Repository

In Dalamud's Plugin Installer settings, add this URL under Custom Plugin Repositories:

```text
https://raw.githubusercontent.com/tancred423/dmu-p4-debuff-helper/refs/heads/main/repo.json
```

Refresh the plugin list, then install `DMU Helper` from Dalamud's plugin installer.
