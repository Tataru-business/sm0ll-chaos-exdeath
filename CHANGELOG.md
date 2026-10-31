# Changelog

## 1.0.20.0

- Removed the Phase 2 media file, floor video and video screen added in 1.0.15.0 to 1.0.19.0. Phase 2 plays the bundled Ride the Rhythm track again, as in 1.0.14.0.
- Kept from 1.0.17.0: the game's own fight music is fully muted while bundled music plays, including the time-stretched BGM and stinger channels.

## 1.0.19.0

- The Phase 2 video now plays on an upright screen 50 yalms south of the arena centre, facing the arena, instead of on the floor. Its bottom edge sits at ground level and its height follows the video's shape. Direction, distance, width, height and opacity can be changed, and the floor placement is still available.
- Characters and anything else closer to the camera stay in front of the screen.

## 1.0.18.0

- The floor video is now rendered on the GPU using the game's depth buffer, so characters and bosses stay in front of it instead of being covered. Turn off "Keep characters in front of the video" if this misbehaves.
- Keep HUD visible now cuts out each drawn piece of game UI exactly, instead of large rectangles around whole windows.
- The video uses anisotropic filtering, so it stays sharp when seen at low camera angles.

## 1.0.17.0

- Fixed rectangular holes in the floor video. Keep HUD visible now cuts out only the parts of game windows that are actually drawn, instead of each window's full declared area.
- The game's own fight music is now fully muted while replacement music plays, including the time-stretched BGM and stinger channels the BGM switch did not cover.

## 1.0.16.0

- The floor video now fits the whole arena by default: a round floor of radius 20 centred on (100, 100), matching the Dancing Mad (Ultimate) arena. Settings saved by 1.0.15.0 are moved to these values once.
- The floor video is placed at ground level automatically, using the lowest point your character stands at. Turn this off to set the height by hand.

## 1.0.15.0

- Added a Phase 2 media file setting under Brainrot. Pick a video or audio file on your PC and its audio replaces the Phase 2 music. The file is played from disk and is never bundled.
- Added an optional Phase 2 floor video. The chosen file's video is drawn on the arena floor at full resolution, kept in sync with the music, with settings for position, size, rotation, round or square shape, opacity and keeping the HUD visible. A preview mode lets you place it outside the fight.

## 1.0.14.0

- Bundled fight music now follows the game's BGM and master volume sliders and pauses when BGM is turned off. The game BGM checkbox is no longer changed by the plugin.

## 1.0.13.0

- Added Brainrot, a single toggle for the P1/P2 model, name, clone and VFX changes, phase music, and P3 slam sound.
- Added Garuda Slipstream VFX in P1 and Dancing Green cleave and tankbuster VFX in P2.

## 1.0.12.0

- P1 Garuda and P2 Dancing Green model options now change the boss names locally.

## 1.0.11.0

- Added optional P1 Garuda and P2 Dancing Green model swaps.
- Added bundled phase music: P1 Fallen Angel, P2 Ride the Rhythm, and P3 onward Circus.
- Added the optional Bonk sound on Kefka's P3 ground slams.

## 1.0.7.0

- Added Chaotic mode.

## 1.0.6.0

- Improved healthbar positioning depending on scale.
