> [!IMPORTANT]
> ## Fork differences
>
> This fork adds the following configurations and additions:
> * P3 and P4 tracking can be disabled completely in case you just need P4 for example.
> * Flood tracking can be disabled to declutter the information as this mech is very easy. Just hides the Wound and AF/BD debuffs.
> * Flood terminology: Use "Blue/Purple" instead of "Black/White" and "Blue Vuln/Purple Vuln" instead of "Black Wound/White Wound" to use the in game colors instead of in game names.
> * Option to add "(Short)/(Long)" to Stack/Spread debuff names. Debuffs < 55s are marked as short and > 55s are long.
> * Option to hide the "Real:/Fake:" labels from the debuffs as the debuffs already say what you have to do. E. g. the Spread debuff shows "Stack" if fake so no need to display "Fake:".
> * Option to use "Motion/Stillness" wording instead of "Move/Stop" for Acceleration Bomb.
> * The preview now respects your config.
> * Option to use "OUT/IN" wording instead of "Away/Towards" for Shriek facing direction.
> * Gaze on you is also shown in active window now to remind you to go in. It will have the label `Go in` with Away/Toward or Out/In direction wording.
> * Opt-in experimental Mana Release tracker with optional in-game diagnostics and a safe-region panel. Tracks what first and second Lightning/Ice was in P4 and also gives you the callout "Stand in None/Lightning/Ice/Both".
>
> <img width="671" height="458" alt="image" src="https://github.com/user-attachments/assets/e2b7f688-84fe-4682-9659-1ceda55aec44" />
>
> Per default this fork behaves like the original plugin. But then you can customise it to your liking.
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
