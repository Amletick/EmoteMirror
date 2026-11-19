using System;
using System.Linq;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.ContextMenu;

namespace EmoteMirror;

// Nickname blacklist: emotes from these players are never mirrored.
// An entry is "First Last" (any world) or "First Last@World" (only that home world). Case-insensitive.
public sealed partial class Plugin
{
    private static string WorldOf(IPlayerCharacter pc) => pc.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty;

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

    internal bool IsBlocked(IPlayerCharacter pc)
    {
        if (cfg.Blacklist.Count == 0) return false;
        var name = pc.Name.TextValue;
        var world = WorldOf(pc);
        return cfg.Blacklist.Any(e => Matches(e, name, world));
    }

    internal bool Block(string entry, bool announce)
    {
        entry = Normalize(entry);
        if (entry.Length == 0 || entry.StartsWith('@')) return false;
        if (cfg.Blacklist.Contains(entry, StringComparer.OrdinalIgnoreCase))
        {
            if (announce) Chat.Print($"[EmoteMirror] {entry} уже в чёрном списке.");
            return false;
        }
        cfg.Blacklist.Add(entry);
        Save();
        // stop mirroring right away if the current source just got blocked
        if (appliedTo != Guid.Empty && Objects[sourceIndex] is IPlayerCharacter src && src.Address == sourceAddr && IsBlocked(src))
            Clear();
        if (announce) Chat.Print($"[EmoteMirror] {entry} добавлен в чёрный список.");
        return true;
    }

    internal bool Unblock(string entry, bool announce)
    {
        entry = Normalize(entry);
        var removed = cfg.Blacklist.RemoveAll(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase));
        if (removed > 0) Save();
        if (announce)
        {
            if (removed > 0) Chat.Print($"[EmoteMirror] {entry} убран из чёрного списка.");
            else Chat.PrintError($"[EmoteMirror] {entry} нет в чёрном списке.");
        }
        return removed > 0;
    }

    private void BlockFromCommand(string rest)
    {
        if (rest.Length > 0) { Block(rest, true); return; }
        if (Targets.Target is IPlayerCharacter pc && pc.Address != Objects.LocalPlayer?.Address)
            Block($"{pc.Name.TextValue}@{WorldOf(pc)}", true);
        else
            Chat.PrintError("[EmoteMirror] Укажи ник (/emotemirror block Имя Фамилия[@Мир]) или возьми игрока в таргет.");
    }

    private void PrintBlacklist()
    {
        Chat.Print(cfg.Blacklist.Count == 0
            ? "[EmoteMirror] Чёрный список пуст."
            : $"[EmoteMirror] Чёрный список ({cfg.Blacklist.Count}): {string.Join(", ", cfg.Blacklist)}");
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Default || args.Target is not MenuTargetDefault t) return;
        if (t.TargetObject is not IPlayerCharacter pc || pc.Address == Objects.LocalPlayer?.Address) return;
        if (IsBlocked(pc))
        {
            var name = pc.Name.TextValue;
            var world = WorldOf(pc);
            args.AddMenuItem(new MenuItem
            {
                Name = "Emote Mirror: убрать из ЧС", PrefixChar = 'E',
                OnClicked = _ =>
                {
                    foreach (var e in cfg.Blacklist.Where(e => Matches(e, name, world)).ToList()) Unblock(e, true);
                },
            });
        }
        else
            args.AddMenuItem(new MenuItem
            {
                Name = "Emote Mirror: в чёрный список", PrefixChar = 'E',
                OnClicked = _ => Block($"{pc.Name.TextValue}@{WorldOf(pc)}", true),
            });
    }
}
