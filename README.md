# Emote Mirror

Dalamud plugin for FFXIV. When another player uses an emote on you, it copies **their modded emote** (animation `.pap`/`.tmb`, VFX, sounds — resolved through their Penumbra collection, e.g. from a Lightless/Mare sync) onto you as a temporary Penumbra mod and replies with the same emote. The temporary mod is removed when your emote ends.

Appearance try-on that used to live here moved to **[Look Mirror](https://github.com/Amletick/LookMirror)**.

## Features
- Copy emote mods aimed at you, optional automatic reply
- Filters: friends only, party only (also works in cross-world parties), cached friend list for duties
- **Whitelist** and **blacklist** by nickname: `Name Surname` (any world) or `Name Surname@World`.
  Blacklist always wins; whitelisted players skip the friend/party filters; optional "only whitelist" mode.
- Option to mute copied sounds; open the window together with the Emote window

## Install
In game: `/xlsettings` → **Experimental** → **Custom Plugin Repositories**, add

```
https://raw.githubusercontent.com/Amletick/EmoteMirror/main/repo.json
```

tick it, **Save**, then find the plugin in `/xlplugins`.

## Commands
`/emotemirror` — window  
`/emotemirror on | off | reply | debug`  
`/emotemirror block | allow [Name Surname[@World]]` — blacklist / whitelist; without a name uses your current target  
`/emotemirror unblock | unallow Name Surname[@World]`  
`/emotemirror blacklist | whitelist` — print a list  
`/emotemirror onlywhite` — toggle "only whitelist"  

Right-click a player → *Emote Mirror: в белый список / в чёрный список* (or remove from a list).

## Requirements
Penumbra. Built with Dalamud.NET.Sdk 15 (`dotnet build -c Release`), load `bin/Release/EmoteMirror.dll` as a dev plugin.
