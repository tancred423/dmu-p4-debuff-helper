> [!IMPORTANT]
> ## Fork differences
>
> This fork adds configurable P3, P4, and Flood tracking; Black/White or Blue/Purple Flood terminology; optional Short/Long Stack/Spread labels; Real/Fake visibility; Motion/Stillness Acceleration Bomb wording; tracking-aware previews; self-gaze `Go in` calls with Away/Toward or Out/In direction wording; and an opt-in experimental Mana Release helper with optional in-game diagnostics and a safe-region panel.
>
> Add this custom repository URL in Dalamud's Plugin Installer settings, under Custom Plugin Repositories, then refresh and install `DMU Helper`:
>
> ```text
> https://raw.githubusercontent.com/tancred423/dmu-p4-debuff-helper/refs/heads/main/repo.json
> ```
>
> Original README below

# DMU Helper

DMU Helper is a Dalamud plugin for P3 Black Hole assignments and P4 debuffs in Dancing Mad Ultimate.

It is DMU-only. The helper window pops into the relevant compact view: P3 Black Hole instructions when your Black Hole assignment is detected, or P4 debuff guidance when watched P4 statuses/tells are detected. Grand Cross and Chaos debuffs use the boss tell status for real/fake calls, while Flood debuffs show side guidance from the player's Wound pairing.

## Current Tracking

- P3 Black Hole: First/Second/Third in Line, Accretion, Primordial Crust, Earth Resistance Down II, and Black Hole hit records.
- Grand Cross: Compressed Water, Forked Lightning, Cursed Shriek, Acceleration Bomb.
- Chaos: Dynamic Fluid, Entropy.
- Flood: Black Wound, White Wound, Allagan Field, Beyond Death.

## Commands

Open settings:

```text
/dmu
/dmuh
/dmuhelper
```

## Dalamud Repository

Add this custom plugin repository URL in Dalamud:

```text
https://puni.sh/api/repository/nainai
```

Then install `DMU Helper` from Dalamud's plugin installer.
