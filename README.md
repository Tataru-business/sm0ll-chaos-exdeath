# Sm0ll Chaos & Exdeath

A small Dalamud dev plugin for **Dancing Mad (Ultimate)**. In territory **1363**, it makes the rendered models of **Chaos** (BattleNpc BaseId 19508) and **Exdeath** (BattleNpc BaseId 19509) smaller on your screen. This can make the bosses less distracting during the fight.

The plugin starts enabled with each model at **60%** of its original render size. The `/dmuscale` settings window has an enable checkbox and separate Chaos and Exdeath sliders from **30% to 100%**.

Only `DrawObject.Scale` is changed. The plugin does not write to hitboxes, positions, targeting state, or combat logic. It reapplies the chosen model scale on `Framework.Update` and restores the captured original scale when disabled, when you leave the duty, or when the plugin unloads while the same model is still present.

## Install through Dalamud

Paste this **custom plugin repository URL** into Dalamud's **Experimental → Custom Plugin Repositories** setting:

`https://raw.githubusercontent.com/Tataru-business/sm0ll-chaos-exdeath/main/repo.json`

Save the setting, open `/xlplugins`, find **Sm0ll Chaos & Exdeath**, and install it. Enter `/dmuscale` in game to adjust the sliders. The feed points Dalamud to the packaged ZIP and allows future version updates through the plugin installer.

If you prefer a manual dev-plugin installation, [download the ZIP directly](https://raw.githubusercontent.com/Tataru-business/sm0ll-chaos-exdeath/main/dist/Sm0ll-Chaos-and-Exdeath-dev-plugin.zip), extract it, then add the extracted `DMUModelScale.dll` under **Experimental → Dev Plugin Locations**.

## Build from source

Install the **.NET 10 SDK** and a current XIVLauncher/Dalamud development distribution. Run `dotnet build -c Release` in this repository. The Dalamud.NET.Sdk packager writes the dev-plugin archive to `bin/Release/DMUModelScale/latest.zip`. If Dalamud is installed in a nonstandard location, set `DALAMUD_HOME` to the extracted Dalamud distribution before building.

The project targets `Dalamud.NET.Sdk/15.0.0`. Game updates may require rebuilding against a matching Dalamud and FFXIVClientStructs version. The territory and NPC IDs are the requested values and have not been checked here in a live duty.

