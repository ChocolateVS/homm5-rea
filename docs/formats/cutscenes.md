# Movies and cut-scenes

## Movies

UIGameRoot (`UI/UIGameRoot.(UIGameRoot).xdb`, root 158 of `Roots/roots.xdb`) has `movies`:
`intro` → `../video/intro_a1.xml`, `outro` → `../video/outro_a1.xml` (paths relative to `bin/`;
referenced by the exe at `00757710` / `00757790`). A movie list (`Base/Movies/Item`) gives `FileName`
(relative to `bin/`, without extension), `CanSkipMovie`, `CanSkipSequence` and optional `SlideShowMode`
(parsed by `0088df10`). The stock intro is `logo-ubi6`, `logo-nival`, `claim`; the GOG build ships the
first two as Ogg Theora files and no `claim`; the outro list is empty. Played by
`game/src/Cinematics/MoviePlayer.cs` on startup (`Boot.cs`): click/Space/Enter skips a movie, Escape the
whole list.

## Splash screen

`UI/AfterGameSplash` (UIGameRoot `ID_SPLASH_SCREEN`) is opened by the console command
`show_splash_screen` (`006b0700` → `006b0590`); its builder (`006b02b0`) collects the screen's `pic1`,
`pic2`, … children in order. The editor's configs set `disable_splash_screen = 1`. Its pictures advertise
the expansion; the client shows them when quitting from the main menu
(`game/src/Cinematics/AfterGameSplash.cs`). *When exactly the original shows it, and for how long each
picture stays, was not traced.*

## In-engine cut-scenes

`StartCutScene` exists in the script API but no shipped script calls it; the only cut-scene data is
`Maps/Cutscenes/Dwelling` (the dwelling fly-by). `UI/CutsceneScreen` (`N_CUTSCENE_SCREEN_ID`) has a
`SubtitlesArea`. Not implemented: the client answers `cut_scene` requests immediately.
