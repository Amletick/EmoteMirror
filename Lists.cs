using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.ContextMenu;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using GameCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace EmoteMirror;

// Black / white lists by nickname and the "who may we copy" decision.
// An entry is "First Last" (any world) or "First Last@World" (only that home world). Case-insensitive.
// Blacklist always wins. Whitelisted players skip the friend / party filters; "only whitelist" drops everyone else.
public sealed unsafe partial class Plugin
{
    internal sealed record NameList(string Title, string Added, Func<Config, List<string>> Get);

    internal static readonly NameList Black = new("Чёрный список", "в чёрный список", c => c.Blacklist);
    internal static readonly NameList White = new("Белый список", "в белый список", c => c.Whitelist);
    private static NameList Other(NameList l) => l == Black ? White : Black;

    private static string WorldOf(IPlayerCharacter pc) => pc.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty;
    private static string Entry(IPlayerCharacter pc) => $"{pc.Name.TextValue}@{WorldOf(pc)}";

    private static string Normalize(string entry)
    {
        entry = entry.Trim();
        var at = entry.IndexOf('@');
        if (at < 0) return string.Join(' ', entry.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var name = string.Join(' ', entry[..at].Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var world = entry[(at + 1)..].Trim();
        return world.Length == 0 ? name : $"{name}@{world}";
    }

    private static bool Matches(string entry, string name, string world)
    {
        var at = entry.IndexOf('@');
        if (at < 0) return string.Equals(entry, name, StringComparison.OrdinalIgnoreCase);
        return string.Equals(entry[..at], name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry[(at + 1)..], world, StringComparison.OrdinalIgnoreCase);
    }

    internal bool InList(NameList l, IPlayerCharacter pc)
    {
        var list = l.Get(cfg);
        if (list.Count == 0) return false;
        var name = pc.Name.TextValue;
        var world = WorldOf(pc);
        return list.Any(e => Matches(e, name, world));
    }

    internal bool IsBlocked(IPlayerCharacter pc) => InList(Black, pc);

    // party check that also works in cross-world parties (StatusFlags.PartyMember is only set for same-world ones)
    private bool IsPartyMember(IPlayerCharacter pc)
    {
        if (pc.StatusFlags.HasFlag(StatusFlags.PartyMember)) return true;
        if (PartyList.Any(m => m.EntityId == pc.EntityId)) return true;
        try
        {
            if (!InfoProxyCrossRealm.IsCrossRealmParty()) return false;
            var cid = ((GameCharacter*)pc.Address)->ContentId;
            if (cid != 0 && InfoProxyCrossRealm.IsContentIdInParty(cid)) return true;
            for (uint i = 0; i < 8; i++)
            {
                var m = InfoProxyCrossRealm.GetGroupMember(i);
                if (m == null) continue;
                if (m->EntityId == pc.EntityId || (cid != 0 && m->ContentId == cid)) return true;
            }
        }
        catch (Exception e) { Log.Debug(e, "cross-realm party check"); }
        return false;
    }

    internal bool Allowed(IPlayerCharacter pc, out string why)
    {
        if (IsBlocked(pc)) { why = "в чёрном списке"; return false; }
        if (InList(White, pc)) { why = "в белом списке"; return true; }
        if (cfg.OnlyWhitelist) { why = "не в белом списке"; return false; }
        var friend = !cfg.OnlyFriends || IsFriend(pc);
        var party = !cfg.OnlyParty || IsPartyMember(pc);
        why = $"friend={friend}, party={party}";
        return friend && party;
    }

    internal bool Add(NameList l, string entry, bool announce)
    {
        entry = Normalize(entry);
        if (entry.Length == 0 || entry.StartsWith('@')) return false;
        var list = l.Get(cfg);
        if (list.Contains(entry, StringComparer.OrdinalIgnoreCase))
        {
            if (announce) Chat.Print($"[EmoteMirror] {entry} уже: {l.Title.ToLowerInvariant()}.");
            return false;
        }
        // one player can't be in both lists
        var moved = Other(l).Get(cfg).RemoveAll(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase)) > 0;
        list.Add(entry);
        Save();
        // stop mirroring right away if the current source is no longer allowed
        if (appliedTo != Guid.Empty && Objects[sourceIndex] is IPlayerCharacter src && src.Address == sourceAddr && !Allowed(src, out _))
            Clear();
        if (announce) Chat.Print($"[EmoteMirror] {entry} добавлен {l.Added}" + (moved ? $" (убран из списка «{Other(l).Title}»)." : "."));
        return true;
    }

    internal bool Remove(NameList l, string entry, bool announce)
    {
        entry = Normalize(entry);
        var removed = l.Get(cfg).RemoveAll(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase));
        if (removed > 0) Save();
        if (announce)
        {
            if (removed > 0) Chat.Print($"[EmoteMirror] {entry} убран из списка «{l.Title}».");
            else Chat.PrintError($"[EmoteMirror] {entry} нет в списке «{l.Title}».");
        }
        return removed > 0;
    }

    private void AddFromCommand(NameList l, string rest)
    {
        if (rest.Length > 0) { Add(l, rest, true); return; }
        if (Targets.Target is IPlayerCharacter pc && pc.Address != Objects.LocalPlayer?.Address)
            Add(l, Entry(pc), true);
        else
            Chat.PrintError("[EmoteMirror] Укажи ник (Имя Фамилия[@Мир]) или возьми игрока в таргет.");
    }

    private void PrintList(NameList l)
    {
        var list = l.Get(cfg);
        Chat.Print(list.Count == 0
            ? $"[EmoteMirror] {l.Title}: пусто."
            : $"[EmoteMirror] {l.Title} ({list.Count}): {string.Join(", ", list)}");
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Default || args.Target is not MenuTargetDefault t) return;
        if (t.TargetObject is not IPlayerCharacter pc || pc.Address == Objects.LocalPlayer?.Address) return;
        var name = pc.Name.TextValue;
        var world = WorldOf(pc);
        foreach (var l in new[] { Black, White })
        {
            if (InList(l, pc))
                args.AddMenuItem(new MenuItem
                {
                    Name = $"Emote Mirror: убрать из списка «{l.Title}»", PrefixChar = 'E',
                    OnClicked = _ => { foreach (var e in l.Get(cfg).Where(e => Matches(e, name, world)).ToList()) Remove(l, e, true); },
                });
            else
                args.AddMenuItem(new MenuItem
                {
                    Name = $"Emote Mirror: {l.Added}", PrefixChar = 'E',
                    OnClicked = _ => Add(l, $"{name}@{world}", true),
                });
        }
    }
}
