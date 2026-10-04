# DMU Helper TODO

## Fast Context

- Plugin name: `DMU Helper`
- Internal name: `DMUP4DebuffHelper`
- Repo: `tancred423/dmu-p4-debuff-helper`
- Dalamud feed URL: `https://raw.githubusercontent.com/tancred423/dmu-p4-debuff-helper/refs/heads/main/repo.json`
- Settings commands: `/dmu`, `/dmuh`, `/dmuhelper`
- DMU territory ID: `1363`

## Implemented

- [x] Initial plugin scaffold.
- [x] DMU-only party status scanning.
- [x] Helper window listing active party statuses, IDs, and timers.
- [x] Debug chat for gained/lost statuses.
- [x] Settings for helper visibility, font scale, background opacity, and debug chat.
- [x] Identified and tracked P4 debuffs:
  - `5545` Compressed Water
  - `5544` Forked Lightning
  - `5543` Cursed Shriek
  - `5546` Acceleration Bomb
  - `5548` Dynamic Fluid
  - `5547` Entropy
  - `4888` Black Wound
  - `4887` White Wound
  - `454` Allagan Field
  - `5464` Beyond Death
- [x] Added boss tell status tracking with `2056` params `1119`, `1120`, `1121`, and `1122`.
- [x] Added real/fake-aware assignment text that preserves the captured state until the debuff falls off.
- [x] Live helper shows the local player's P4 debuffs as compact icons with details on hover.
- [x] Added Buff Summary tab with current/recorded pull groups, boss tell history, and captured debuff records.
- [x] Added helper-window preview mode for inactive testing.
- [x] Combined P3 Black Hole and P4 debuff display into one compact DMU Helper window.
- [x] Uses BossMod naming for P3 `5454` as Primordial Crust.

## Next Work

- [ ] Live-test the boss tell param mapping and confirm every real/fake state against observed pulls.
- [ ] Decide whether to add Kefka lockon/headmarker event capture for `675`, `676`, `677`, and `678`.
- [ ] Refine player-specific instructions after the group strat is chosen.
- [ ] Add screenshots after the helper has its real P4 behavior.
