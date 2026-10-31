# Sm0ll Chaos & Exdeath

A small Dalamud plugin for **Dancing Mad (Ultimate)**. In territory **1363**, it makes the rendered models of **Chaos** (BattleNpc BaseId 19508) and **Exdeath** (BattleNpc BaseId 19509) smaller on your screen. This can make the bosses less distracting during the fight.

The plugin starts enabled with each model at **60%** of its original render size. The `/dmuscale` settings window has an enable checkbox, separate Chaos and Exdeath sliders from **30% to 100%**, a **Chaotic mode** checkbox, and a **Move floating lifebar with model** checkbox at the bottom. The lifebar checkbox is on by default. Turning it off restores the original lifebar height while keeping the chosen model sizes.

Chaotic mode is off by default. When enabled, Chaos uses **0.30** while not casting and **1.00** while casting. Its normal size slider is disabled until the mode is turned off. Exdeath keeps its own slider setting. This cast-driven behavior needs an in-game check.

The model size uses `DrawObject.Scale`. When the optional lifebar checkbox is on, the plugin also adjusts `NameplateOffsetTarget.Y` to move the floating name and lifebar. Its fixed height adjustment is **−0.33** relative to the model scale. The plugin does not write to hitboxes, positions, targeting state, or combat logic. It reapplies the chosen settings on `Framework.Update` and restores the captured originals when disabled, when you leave the duty, or when the plugin unloads while the same model is still present. The lifebar adjustment still needs an in-game check in the duty.

## Phase 2 media file

Under **Brainrot → Phase 2 media file** you can pick a video or audio file on your own PC (MP4, MKV, WebM, MOV, M4A, MP3, FLAC, WAV and other formats Windows can play). The plugin plays it from that file; nothing is uploaded or bundled.

- **Use its audio as the Phase 2 music** replaces the bundled Phase 2 track. It follows the game's BGM volume like the other tracks. If the file cannot be played, the bundled track is used.
- **Show its video on the arena floor** draws the video flat on the floor during Phase 2, decoded at the file's full resolution with Windows Media Foundation. It follows the music's position while the music plays, and loops on its own otherwise.

The floor video is a screen overlay, not a change to the game's floor. It is drawn over players, effects and ground markers, so lower **Opacity** if mechanics get hard to read. **Keep HUD visible** leaves gaps around game windows such as hotbars and the party list. The default floor is a 40-yalm square centred on (100, 100). Turn on **Preview the floor here** and use **Center on me** and the sliders to place it, as the Phase 2 arena layout has not been checked in a live duty. WebM and MKV playback needs the codecs Windows uses for those formats (for example, Web Media Extensions for VP9/Opus).

## Install through Dalamud

Paste this **custom plugin repository URL** into Dalamud's **Experimental → Custom Plugin Repositories** setting:

`https://raw.githubusercontent.com/Tataru-business/sm0ll-chaos-exdeath/main/repo.json`

Save the setting, open `/xlplugins`, find **Sm0ll Chaos & Exdeath**, and install it. Enter `/dmuscale` in game to adjust the sliders. The feed points Dalamud to the packaged ZIP and allows future version updates through the plugin installer.

## Build from source

Install the **.NET 10 SDK** and a current XIVLauncher/Dalamud development distribution. Run `dotnet build -c Release` in this repository. The Dalamud.NET.Sdk packager writes the dev-plugin archive to `bin/Release/DMUModelScale/latest.zip`. If Dalamud is installed in a nonstandard location, set `DALAMUD_HOME` to the extracted Dalamud distribution before building.

The project targets `Dalamud.NET.Sdk/15.0.0`. Game updates may require rebuilding against a matching Dalamud and FFXIVClientStructs version. The territory and NPC IDs are the requested values and have not been checked here in a live duty.

See the [changelog](CHANGELOG.md) for version changes.
