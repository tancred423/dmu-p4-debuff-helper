> [!IMPORTANT]
> ## Fork differences
>
> This fork adds the following configurations and additions:
> * P3 and P4 tracking can be disabled completely in case you just need P4 for example.
> * Wound tracking can be hidden so it just shows the "Go Black/Blue" debuff (PD/AF), but not the White/Black Wound debuff.
> * Flood terminology: Use "Blue/Purple" instead of "Black/White" and "Blue Vuln/Purple Vuln" instead of "Black Wound/White Wound" to use the in game colors instead of in game names.
> * Option to add "(Short)/(Long)" to Stack/Spread and personal Gaze callouts. Stack/Spread debuffs < 55s are marked as short and > 55s are long; the first Gaze cohort is short and the second is long.
> * Option to hide the "Real:/Fake:" labels from the debuffs as the debuffs already say what you have to do. E. g. the Spread debuff shows "Stack" if fake so no need to display "Fake:".
> * Option to use "Motion/Stillness" wording instead of "Move/Stop" for Acceleration Bomb.
> * The preview now respects your config.
> * Option to use "OUT/IN" wording instead of "Away/Towards" for Shriek facing direction.
> * Gaze on you is also shown in active window now to remind you to go in. It will have the label `Go in` with Away/Toward or Out/In direction wording.
> * Opt-in experimental Mana Release tracker with optional in-game diagnostics and a safe-region panel. Tracks what first and second Lightning/Ice was in P4 and also gives you the callout "Stand in None/Lightning/Ice/Both".
> * Optional vertical P4 layout: Active calls stack on the left, with Gazes and Mana Release on the right.
> * Opt-in P4 reminder checklist above Active: shows your current short/long Bomb, Stack/Spread, and Gaze assignments without timers.
> * Wound rows can be hidden from Active without disabling Flood destination guidance.
> * Opt-in P4 session logging records your resolved personal assignments, both gaze cohorts, and Mana Release conclusion in memory for review after wipes or leaving the instance.
> * Acceleration Bomb callouts always use uppercase MOVE/MOTION/STOP/STILLNESS, with an opt-in green/red color treatment.
> * Optional referred stack call. If you got short stack/spread a long stack will be added too and vice versa.
>
> Example with horizontal layout, descriptive colors, added "(Short)/(Long)" text, shriek callout in active window, shriek call out change to OUT/IN, removed "Real/Fake" prefix, added Checklist, using Motion/Stillness instead of Move/Stop, green/red colors for Motion/Stillness and referred long stack:
> 
> <img width="643" height="498" alt="image" src="https://github.com/user-attachments/assets/cb3449b2-5825-4813-9f1f-a75b2143cefd" />
>
> Same example but with vertical table view:
> 
> <img width="634" height="415" alt="image" src="https://github.com/user-attachments/assets/856916ad-71a4-4ed9-89b9-d648be37c0d2" />
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
