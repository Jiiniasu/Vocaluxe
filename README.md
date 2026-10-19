![Vocaluxe Logo](https://raw.githubusercontent.com/Vocaluxe/Vocaluxe/develop/Output/Graphics/Logo.png)  

[![Build status](https://ci.appveyor.com/api/projects/status/y9ylgebv14i7usxx/branch/develop?svg=true)](https://ci.appveyor.com/project/VocaluxeBuildBot/vocaluxe/branch/develop)
[![Nightly build](https://img.shields.io/badge/nightly%20build-latest-blue.svg)](https://vocaluxe.org/#download)
[![Latest stable release](https://img.shields.io/github/release/vocaluxe/Vocaluxe.svg?label=stable%20release)](https://vocaluxe.org/#download)
[![Known bugs](https://img.shields.io/github/issues-raw/vocaluxe/vocaluxe/bug.svg?label=known%20bugs)](https://github.com/Vocaluxe/Vocaluxe/labels/bug)
[![Feature requests](https://img.shields.io/github/issues-raw/vocaluxe/vocaluxe/feature%20request.svg?label=feature%20requests&colorB=007ec6)](https://github.com/Vocaluxe/Vocaluxe/labels/feature%20request)
[![License](https://img.shields.io/badge/licence-GPLv3-blue.svg)](https://github.com/Vocaluxe/Vocaluxe/blob/develop/LICENSE.txt)  

[1. About](#1-about)                     
[2. Download](#2-download)                     
[3. Contributing](#3-contributing)        
[4. Help & Support](#4-help--support)  
[5. Multi-monitor](#5-multi-monitor)

## 1. About
Vocaluxe is a free and open source singing game, inspired by the original UltraStar (developed by corvus5) and the great Ultrastar Deluxe project - yet it is an entirely new development in C#.   
It allows up to six players to sing along with music using microphones in order to score points, depending on the pitch of the voice and the rhythm of singing.
It features mouse, keyboard, gamepad and Wiimote navigation, a multi-language interface, and singer profiles with avatars and supports songs in CP1250/52 and UTF8 encodings.   
Vocaluxe offers diversified game modes and can optionally sync profiles and high scores via a cloud service.

Supported Operating Systems / Requirements:
- Windows with the .NET 10 runtime (see [HowToBuildWin](HowToBuildWin.md) to build it yourself)
- Linux x64 with the .NET 10 SDK/runtime (see [HowToBuildLinux](HowToBuildLinux.md) to build it yourself)
- 1 GHz CPU, 512 MB RAM, Graphics card with OpenGL support


## 2. Download                
You can download precompiled Windows binaries at here:
https://vocaluxe.org/#download

## 3. Contributing
You want to fix a bug or add a cool new feature to Vocaluxe? Awesome!

### Build Vocaluxe
Building Vocaluxe is easy:   
Just follow the guide on our wiki to clone the repository and create your own Vocaluxe build:   
[Build Vocaluxe (Windows)](https://github.com/Vocaluxe/Vocaluxe/wiki/HowTo:-Build-Vocaluxe-(Windows))

### Submit your change
Upload your changes to your fork and open a new pull request.

The development of Vocaluxe uses this git branching model:   
http://nvie.com/posts/a-successful-git-branching-model/

### Questions
You have a problem or a question implementing your change?   
Just [open an issue](https://github.com/Vocaluxe/Vocaluxe/issues/new) or mention `@Vocaluxe` in a comment on GitHub.

## 4. Help & Support
Vocaluxe Homepage: https://vocaluxe.org/  
GitHub Wiki:			https://github.com/Vocaluxe/Vocaluxe/wiki   
GitHub Issues Tracker:	https://github.com/Vocaluxe/Vocaluxe/issues   
Translations:			https://www.transifex.com/projects/p/vocaluxe/   
Support Forum (German):	http://www.ultra-star.de   
Song-Database (USDB):	http://usdb.animux.de/

## 5. Multi-monitor
Vocaluxe renders one game canvas per screen (1-6, `NumScreens`). Two display paths exist in
`Config.xml`:

- `MultiWindow = ON` (default): one borderless window **per monitor**, all sharing a single OpenGL
  context. Each window handles its own input and renders its own panel, so you do not need
  Eyefinity/Surround or a single giant panel. More panels than monitors are stacked (cascaded)
  on the first monitors.
- `MultiWindow = OFF`: the legacy single giant window (`NumScreens` panels wide) - requires
  Eyefinity/Surround, or a single monitor, for `NumScreens > 1`.

If the windows would land on graphics adapters from different vendors (mixed GPU), Vocaluxe warns
and falls back to the single giant window for that session.
