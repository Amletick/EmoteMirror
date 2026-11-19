# Emote Mirror

Dalamud plugin for FFXIV. When another player uses an emote on you, it copies **their modded emote** (animation `.pap`/`.tmb`, VFX, sounds — resolved through their Penumbra collection, e.g. from a Lightless/Mare sync) onto you as a temporary Penumbra mod and replies with the same emote. The temporary mod is removed when your emote ends.

Appearance try-on that used to live here moved to **[Look Mirror](https://github.com/Amletick/LookMirror)**.

## Features
- Copy emote mods aimed at you, optional automatic reply
- Filters: friends only, party only, cached friend list for duties
- **Nickname blacklist**: `Name Surname` (any world) or `Name Surname@World`
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
`/emotemirror block [Name Surname[@World]]` — without a name blocks your current target  
`/emotemirror unblock Name Surname[@World]`  
`/emotemirror blacklist` — print the list  

Right-click a player → *Emote Mirror: в чёрный список* / *убрать из ЧС*.

## Requirements
Penumbra. Built with Dalamud.NET.Sdk 15 (`dotnet build -c Release`), load `bin/Release/EmoteMirror.dll` as a dev plugin.
