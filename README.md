# Sm0ll Chaos & Exdeath

A small Dalamud plugin for **Dancing Mad (Ultimate)**. In territory **1363**, it makes the rendered models of **Chaos** (BattleNpc BaseId 19508) and **Exdeath** (BattleNpc BaseId 19509) smaller on your screen. This can make the bosses less distracting during the fight.

The plugin starts enabled with each model at **60%** of its original render size. The `/dmuscale` settings window has an enable checkbox, separate Chaos and Exdeath sliders from **30% to 100%**, and a **Move floating lifebar with model** checkbox. The lifebar checkbox is on by default; turning it off restores the original lifebar height while keeping the chosen model sizes.

The model size uses `DrawObject.Scale`. When the optional lifebar checkbox is on, the plugin also adjusts `NameplateOffsetTarget.Y` to lower the floating name and lifebar toward the smaller model. It does not write to hitboxes, positions, targeting state, or combat logic. It reapplies the chosen settings on `Framework.Update` and restores the captured originals when disabled, when you leave the duty, or when the plugin unloads while the same model is still present. The lifebar adjustment still needs an in-game check in the duty.

## Install through Dalamud

Paste this **custom plugin repository URL** into Dalamud's **Experimental → Custom Plugin Repositories** setting:

`https://raw.githubusercontent.com/Tataru-business/sm0ll-chaos-exdeath/main/repo.json`

Save the setting, open `/xlplugins`, find **Sm0ll Chaos & Exdeath**, and install it. Enter `/dmuscale` in game to adjust the sliders. The feed points Dalamud to the packaged ZIP and allows future version updates through the plugin installer.

## Build from source

Install the **.NET 10 SDK** and a current XIVLauncher/Dalamud development distribution. Run `dotnet build -c Release` in this repository. The Dalamud.NET.Sdk packager writes the dev-plugin archive to `bin/Release/DMUModelScale/latest.zip`. If Dalamud is installed in a nonstandard location, set `DALAMUD_HOME` to the extracted Dalamud distribution before building.

The project targets `Dalamud.NET.Sdk/15.0.0`. Game updates may require rebuilding against a matching Dalamud and FFXIVClientStructs version. The territory and NPC IDs are the requested values and have not been checked here in a live duty.

