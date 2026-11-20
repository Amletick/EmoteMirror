using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Configuration;
using Dalamud.Game.Command;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using Penumbra.Api.Enums;
using Dalamud.Plugin.Ipc;
using Penumbra.Api.IpcSubscribers;
using GameCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace EmoteMirror;

[Serializable]
public class Config : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public bool ReplyMyself { get; set; } = true;
    public bool Debug { get; set; } = false;
    public int WaitForReplySeconds { get; set; } = 15;
    public bool OnlyFriends { get; set; } = false;
    public bool OnlyParty { get; set; } = false;
    public bool SyncFriends { get; set; } = false;
    public List<string> CachedFriends { get; set; } = new();
    public bool OpenWithEmoteWindow { get; set; } = false;
    public bool MuteCopiedSound { get; set; } = false;
    public List<string> Blacklist { get; set; } = new();
    public List<string> Whitelist { get; set; } = new();
    public bool OnlyWhitelist { get; set; } = false;
}

public sealed unsafe partial class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface Pi { get; private set; } = null!;
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    [PluginService] internal static IDataManager Data { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider Interop { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static ITargetManager Targets { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;

    private const string Tag = "EmoteMirror";
    private const int Priority = 9999;
    private static readonly string[] RaceCodes =
        { "c0101", "c0201", "c0301", "c0401", "c0501", "c0601", "c0701", "c0801", "c0901", "c1001", "c1101", "c1201",
          "c1301", "c1401", "c1501", "c1601", "c1701", "c1801" };
    private static readonly string[] CapturedExt = { ".pap", ".tmb", ".avfx", ".atex", ".scd", ".shpk" };

    private delegate void OnEmoteDelegate(ulong unk, ulong instigatorAddr, ushort emoteId, ulong targetId, ulong unk2);
    private readonly Hook<OnEmoteDelegate>? emoteHook;

    private readonly Config cfg;
    private readonly ResolveGameObjectPath resolve;
    private readonly GetCollectionForObject collectionFor;
    private readonly AddTemporaryMod addTemp;
    private readonly RemoveTemporaryMod removeTemp;
    private readonly ICallGateSubscriber<nint, string, string, object?> resolvedEvent;
    private readonly ICallGateSubscriber<nint, int, object?> redrawnEvent;
    private readonly RedrawObject redraw;

    // restart of a dance that is already playing: the game keeps the old animation loaded, so (like Encore) we redraw
    // ourselves, which cancels the emote and drops the loaded files, then play the emote again with the new files
    private enum Restart { None, NeedRedraw, WaitRedraw, WaitEmote }
    private Restart restart;
    private DateTime restartAt;

    // state of the current copy
    private nint sourceAddr;
    private ushort sourceEmote;
    private nint pendingSourceAddr;
    private ushort pendingSourceEmote;
    private DateTime pendingUntil;
    private Guid appliedTo;
    private DateTime waitReplyUntil;
    private bool replyStarted, pendingReply;
    private readonly Dictionary<string, string> paths = new(StringComparer.OrdinalIgnoreCase);

    public Plugin()
    {
        cfg = Pi.GetPluginConfig() as Config ?? new Config();
        cfg.ReplyMyself = true; // migrate old installations that delegated replies
        resolve = new ResolveGameObjectPath(Pi);
        collectionFor = new GetCollectionForObject(Pi);
        addTemp = new AddTemporaryMod(Pi);
        removeTemp = new RemoveTemporaryMod(Pi);
        resolvedEvent = Pi.GetIpcSubscriber<nint, string, string, object?>(GameObjectResourcePathResolved.Label);
        resolvedEvent.Subscribe(OnResolved);
        redrawnEvent = Pi.GetIpcSubscriber<nint, int, object?>(GameObjectRedrawn.Label);
        redrawnEvent.Subscribe(OnRedrawn);
        redraw = new RedrawObject(Pi);
        try
        {
            emoteHook = Interop.HookFromSignature<OnEmoteDelegate>("E8 ?? ?? ?? ?? 48 8D 8B ?? ?? ?? ?? 4C 89 74 24", OnEmoteDetour);
            emoteHook.Enable();
        }
        catch (Exception e)
        {
            Log.Error(e, "emote hook signature not found");
            Chat.PrintError("[EmoteMirror] Не нашёл функцию эмоций в игре (сигнатура устарела).");
        }
        Framework.Update += OnUpdate;
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Emote", OnEmoteWindowSetup);
        ContextMenu.OnMenuOpened += OnMenuOpened;
        InitUi();
        Commands.AddHandler("/emotemirror", new CommandInfo(OnCommand)
        {
            HelpMessage = "on | off | reply (вкл/выкл самостоятельный ответ) | debug | block/allow [Имя Фамилия[@Мир]] (без имени — таргет) | unblock/unallow <имя> | blacklist | whitelist | onlywhite",
        });
    }

    public void Dispose()
    {
        Commands.RemoveHandler("/emotemirror");
        ContextMenu.OnMenuOpened -= OnMenuOpened;
        DisposeUi();
        Framework.Update -= OnUpdate;
        AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "Emote", OnEmoteWindowSetup);
        emoteHook?.Dispose();
        resolvedEvent.Unsubscribe(OnResolved);
        redrawnEvent.Unsubscribe(OnRedrawn);
        Clear();
    }

    private void Save() => Pi.SavePluginConfig(cfg);
    private void Dbg(string s) { Log.Debug(s); if (cfg.Debug) Chat.Print("[EmoteMirror] " + s); }

    private void OnCommand(string cmd, string args)
    {
        var a = args.Trim();
        var sp = a.IndexOf(' ');
        var verb = (sp < 0 ? a : a[..sp]).ToLowerInvariant();
        var rest = sp < 0 ? string.Empty : a[(sp + 1)..].Trim();
        switch (verb)
        {
            case "block": AddFromCommand(Black, rest); return;
            case "unblock": Remove(Black, rest, true); return;
            case "blacklist": PrintList(Black); return;
            case "allow": AddFromCommand(White, rest); return;
            case "unallow": Remove(White, rest, true); return;
            case "whitelist": PrintList(White); return;
            case "onlywhite":
                cfg.OnlyWhitelist = !cfg.OnlyWhitelist; Save();
                Chat.Print($"[EmoteMirror] только белый список: {(cfg.OnlyWhitelist ? "да" : "нет")}");
                return;
            case "on": cfg.Enabled = true; break;
            case "off": cfg.Enabled = false; Clear(); break;
            case "reply": cfg.ReplyMyself = !cfg.ReplyMyself; break;
            case "debug": cfg.Debug = !cfg.Debug; break;
            case "": ToggleUi(); return;
        }
        Save();
        Chat.Print($"[EmoteMirror] {(cfg.Enabled ? "включён" : "выключен")}, отвечаю сам: {(cfg.ReplyMyself ? "да" : "нет")}, отладка: {(cfg.Debug ? "да" : "нет")}" +
                   (appliedTo != Guid.Empty ? $", сейчас скопировано файлов: {paths.Count}" : ""));
    }

    private void OnEmoteWindowSetup(AddonEvent type, AddonArgs args)
    {
        if (cfg.OpenWithEmoteWindow && mainWindow != null)
            mainWindow.IsOpen = true;
    }

    // ---- detection -------------------------------------------------------------------------------------------------

    private void OnEmoteDetour(ulong unk, ulong instigatorAddr, ushort emoteId, ulong targetId, ulong unk2)
    {
        try
        {
            var me = Objects.LocalPlayer;
            if (cfg.Enabled && me != null && targetId == me.GameObjectId && instigatorAddr != (ulong)me.Address)
            {
                var src = Objects.FirstOrDefault(o => (ulong)o.Address == instigatorAddr);
                if (src is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc)
                {
                    var worldName = pc.HomeWorld.ValueNullable?.Name.ExtractText() ?? "?";
                    var allowed = Allowed(pc, out var why);
                    Dbg($"эмоция: {pc.Name.TextValue} @ {worldName} (worldId={pc.HomeWorld.RowId}), {why}, flags={pc.StatusFlags}");
                    if (allowed) Start(src.Address, src.ObjectIndex, emoteId);
                }
                else if (src == null)
                {
                    // In instanced duties the emote packet can arrive before the
                    // actor is inserted into Dalamud's object table. Retry briefly.
                    pendingSourceAddr = (nint)instigatorAddr;
                    pendingSourceEmote = emoteId;
                    pendingUntil = DateTime.UtcNow.AddSeconds(1.5);
                    Dbg("эмоция: источник ещё не найден в ObjectTable, поставил повторную проверку");
                }
            }
            else if (cfg.Debug && cfg.Enabled && me != null)
                Dbg($"эмоция получена, но цель не совпала: target={targetId}, me={me.GameObjectId}");
        }
        catch (Exception e) { Log.Error(e, "OnEmote"); }
        emoteHook!.Original(unk, instigatorAddr, emoteId, targetId, unk2);
    }

    private bool IsFriend(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc)
    {
        if (pc.StatusFlags.HasFlag(Dalamud.Game.ClientState.Objects.Enums.StatusFlags.Friend)) return true;
        if (cfg.SyncFriends && cfg.CachedFriends.Contains($"{pc.Name.TextValue}@{pc.HomeWorld.RowId}", StringComparer.OrdinalIgnoreCase)) return true;
        var info = InfoModule.Instance();
        if (info == null) return false;
        var list = (InfoProxyFriendList*)info->GetInfoProxyById(InfoProxyId.FriendList);
        if (list == null) return false;
        var name = System.Text.Encoding.UTF8.GetBytes(pc.Name.TextValue + "\0");
        fixed (byte* ptr = name)
            return list->GetEntryByName(ptr, (ushort)pc.HomeWorld.RowId) != null;
    }

    private void SyncFriends()
    {
        if (!cfg.SyncFriends || DutyState.IsDutyStarted || DateTime.UtcNow < nextFriendSync) return;
        nextFriendSync = DateTime.UtcNow.AddSeconds(10);
        var info = InfoModule.Instance();
        if (info == null) return;
        var list = (InfoProxyFriendList*)info->GetInfoProxyById(InfoProxyId.FriendList);
        if (list == null || list->GetEntryCount() == 0) return;
        var fresh = new List<string>();
        for (uint i = 0; i < list->GetEntryCount(); i++)
        {
            var entry = list->GetEntry(i);
            if (entry == null || string.IsNullOrWhiteSpace(entry->NameString)) continue;
            fresh.Add($"{entry->NameString}@{entry->HomeWorld}");
        }
        if (fresh.Count == 0) return;
        cfg.CachedFriends = fresh.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Save();
        Dbg($"список друзей синхронизирован: {cfg.CachedFriends.Count}");
    }

    // everything the emoter loads while we mirror them (vfx, sounds, paps) that comes from a mod
    private void OnResolved(nint obj, string gamePath, string fullPath)
    {
        if (obj == 0 || obj != sourceAddr) return;
        if (!CapturedExt.Any(e => gamePath.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return;
        if (cfg.MuteCopiedSound && gamePath.EndsWith(".scd", StringComparison.OrdinalIgnoreCase)) return;
        var file = LocalFile(fullPath);
        if (file == null) return;
        var key = KeyOf(gamePath);
        if (key != null)
        {
            // a pap of the emote we are mirroring: a new file here means their dance mod arrived or changed
            if (!papFiles.TryGetValue(key, out var old) || !string.Equals(old, file, StringComparison.OrdinalIgnoreCase))
            {
                papFiles[key] = file;
                Dbg($"новый файл анимации от игрока: {key} -> {Path.GetFileName(file)}");
                papChanged = true;
            }
        }
        else if (!extras.TryGetValue(gamePath, out var old2) || old2 != file)
        {
            extras[gamePath] = file;
            dirty = true;
        }
    }

    private bool dirty, papChanged;
    private ushort sourceIndex;
    private readonly List<string> emoteKeys = new();
    private readonly Dictionary<string, string> papFiles = new();
    private readonly Dictionary<string, string> extras = new(StringComparer.OrdinalIgnoreCase);
    private DateTime nextCheck, lastRestart, nextFriendSync;
    private int restarts;
    private const int MaxRestarts = 6;

    // ---- copying ---------------------------------------------------------------------------------------------------

    private void Start(nint addr, ushort objIndex, ushort emoteId)
    {
        var me = Objects.LocalPlayer!;
        bool wasEmoting = IsEmoting(me.Address);
        Clear();
        var (valid, _, coll) = collectionFor.Invoke(me.ObjectIndex);
        if (!valid) return;
        var emote = Data.GetExcelSheet<Emote>().GetRowOrDefault(emoteId);
        if (emote == null) return;
        sourceAddr = addr;
        sourceIndex = objIndex;
        sourceEmote = emoteId;
        appliedTo = coll.Id;
        waitReplyUntil = DateTime.UtcNow.AddSeconds(cfg.WaitForReplySeconds);
        nextCheck = DateTime.UtcNow.AddSeconds(1);
        replyStarted = false;
        restarts = 0;
        foreach (var tl in emote.Value.ActionTimeline)
        {
            if (tl.RowId == 0 || !tl.IsValid) continue;
            var key = tl.Value.Key.ExtractText();
            if (!string.IsNullOrEmpty(key) && !emoteKeys.Contains(key)) emoteKeys.Add(key);
        }
        foreach (var (k, f) in ResolvePaps()) { papFiles[k] = f; Dbg($"{k} -> {Path.GetFileName(f)}"); }
        if (wasEmoting) { restart = Restart.NeedRedraw; Dbg("уже в эмоции: перезапускаю через перерисовку"); }
        else pendingReply = true;
        if (papFiles.Count > 0) Apply();
        else Dbg($"у {Objects[objIndex]?.Name} эмоция {emote.Value.Name.ExtractText()} без мода (пока что), жду догрузку");
    }

    private Dictionary<string, string> ResolvePaps()
    {
        var res = new Dictionary<string, string>();
        var me = Objects.LocalPlayer;
        if (me == null) return res;
        var order = new[] { RaceCode(me.Address), RaceCode(sourceAddr) }.Where(r => r != null).Concat(RaceCodes).Distinct().ToList();
        foreach (var key in emoteKeys)
            foreach (var r in order)
            {
                var file = LocalFile(resolve.Invoke(PapPath(r!, key), sourceIndex));
                if (file != null) { res[key] = file; break; }
            }
        return res;
    }

    private string? KeyOf(string gamePath)
    {
        foreach (var key in emoteKeys)
            if (gamePath.EndsWith($"/animation/a0001/bt_common/{key}.pap", StringComparison.OrdinalIgnoreCase)
                && gamePath.StartsWith("chara/human/", StringComparison.OrdinalIgnoreCase))
                return key;
        return null;
    }

    private void Apply()
    {
        dirty = false;
        if (appliedTo == Guid.Empty) return;
        paths.Clear();
        foreach (var (k, f) in papFiles)
            foreach (var r in RaceCodes) paths[PapPath(r, k)] = f;
        foreach (var (gp, f) in extras)
        {
            if (cfg.MuteCopiedSound && gp.EndsWith(".scd", StringComparison.OrdinalIgnoreCase)) continue;
            paths.TryAdd(gp, f);
            if (gp.EndsWith(".pap", StringComparison.OrdinalIgnoreCase))
                foreach (var p in AllRaces(gp)) paths.TryAdd(p, f);
        }
        removeTemp.Invoke(Tag, appliedTo, Priority);
        if (paths.Count == 0) return;
        var ec = addTemp.Invoke(Tag, appliedTo, new Dictionary<string, string>(paths), string.Empty, Priority);
        Dbg($"скопировано {paths.Count} путей: {ec}");
    }

    private void Clear()
    {
        if (appliedTo != Guid.Empty) removeTemp.Invoke(Tag, appliedTo, Priority);
        appliedTo = Guid.Empty;
        sourceAddr = 0;
        paths.Clear();
        papFiles.Clear();
        extras.Clear();
        emoteKeys.Clear();
        dirty = papChanged = pendingReply = false;
        restart = Restart.None;
        pendingSourceAddr = 0;
        pendingSourceEmote = 0;
        pendingUntil = DateTime.MinValue;
    }

    private void OnRedrawn(nint obj, int index)
    {
        if (restart == Restart.WaitRedraw && index == 0)
        {
            restart = Restart.WaitEmote;
            restartAt = DateTime.UtcNow.AddMilliseconds(300);
        }
        if (obj != 0 && obj == sourceAddr)
            nextCheck = DateTime.UtcNow.AddMilliseconds(200);
    }

    private static bool IsEmoting(nint charAddr)
    {
        var ec = &((GameCharacter*)charAddr)->EmoteController;
        return ec->EmoteId != 0 && ec->IsEmoting();
    }

    private void OnUpdate(IFramework _)
    {
        var me = Objects.LocalPlayer;
        if (me == null) { Clear(); return; }
        SyncFriends();
        if (pendingSourceAddr != 0)
        {
            if (DateTime.UtcNow > pendingUntil)
            {
                pendingSourceAddr = 0;
            }
            else
            {
                var pending = Objects.FirstOrDefault(o => (nint)o.Address == pendingSourceAddr);
                if (pending is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc
                    && Allowed(pc, out var _why))
                {
                    var addr = pending.Address;
                    var idx = pending.ObjectIndex;
                    var emote = pendingSourceEmote;
                    pendingSourceAddr = 0;
                    Start(addr, idx, emote);
                }
            }
        }
        if (appliedTo == Guid.Empty) return;
        if (DateTime.UtcNow >= nextCheck)
        {
            nextCheck = DateTime.UtcNow.AddSeconds(1);
            var src = Objects[sourceIndex];
            if (src != null && src.Address == sourceAddr)
                foreach (var (k, f) in ResolvePaps())
                    if (!papFiles.TryGetValue(k, out var old) || !string.Equals(old, f, StringComparison.OrdinalIgnoreCase))
                    {
                        papFiles[k] = f;
                        Dbg($"мод игрока догрузился: {k} -> {Path.GetFileName(f)}");
                        papChanged = true;
                    }
        }
        if (papChanged)
        {
            papChanged = false;
            Apply();
            if (restart == Restart.None && IsEmoting(me.Address) && restarts < MaxRestarts)
            {
                restarts++;
                restart = Restart.NeedRedraw;
                restartAt = lastRestart.AddSeconds(1.5);
                Dbg("перезапускаю эмоцию с новым модом");
            }
        }
        if (dirty) Apply();
        if (pendingReply)
        {
            pendingReply = false;
            Reply();
        }
        switch (restart)
        {
            case Restart.NeedRedraw:
                if (DateTime.UtcNow < restartAt) return;
                lastRestart = DateTime.UtcNow;
                redraw.Invoke(me.ObjectIndex, RedrawType.Redraw);
                restart = Restart.WaitRedraw;
                restartAt = DateTime.UtcNow.AddSeconds(3);
                return;
            case Restart.WaitRedraw:
                if (DateTime.UtcNow < restartAt) return;
                restart = Restart.WaitEmote;
                return;
            case Restart.WaitEmote:
                if (DateTime.UtcNow < restartAt) return;
                restart = Restart.None;
                replyStarted = false;
                waitReplyUntil = DateTime.UtcNow.AddSeconds(cfg.WaitForReplySeconds);
                if (cfg.ReplyMyself) Reply();
                return;
        }
        bool emoting = IsEmoting(me.Address);
        if (emoting) replyStarted = true;
        if ((replyStarted && !emoting) || (!replyStarted && DateTime.UtcNow > waitReplyUntil))
        {
            Dbg("эмоция закончилась, мод снят");
            Clear();
        }
    }

    private void Reply()
    {
        var em = EmoteManager.Instance();
        if (em != null && em->ExecuteEmote(sourceEmote, null)) return;
        // fallback: the chat command
        var emote = Data.GetExcelSheet<Emote>().GetRowOrDefault(sourceEmote);
        var cmd = emote?.TextCommand.ValueNullable?.Command.ExtractText();
        if (string.IsNullOrEmpty(cmd)) return;
        var s = Utf8String.FromString(cmd);
        UIModule.Instance()->ProcessChatBoxEntry(s);
        s->Dtor(true);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static string PapPath(string race, string key) => $"chara/human/{race}/animation/a0001/bt_common/{key}.pap";

    private static IEnumerable<string> AllRaces(string gamePath)
    {
        var m = System.Text.RegularExpressions.Regex.Match(gamePath, @"^chara/human/c\d{4}/");
        if (!m.Success) yield break;
        foreach (var r in RaceCodes) yield return $"chara/human/{r}/" + gamePath[m.Length..];
    }

    private static string? RaceCode(nint charAddr)
    {
        var dobj = ((GameCharacter*)charAddr)->GameObject.DrawObject;
        if (dobj == null || dobj->Object.GetObjectType() != ObjectType.CharacterBase) return null;
        var cb = (CharacterBase*)dobj;
        if (cb->GetModelType() != CharacterBase.ModelType.Human) return null;
        return $"c{((Human*)cb)->RaceSexId:D4}";
    }

    // Penumbra returns the game path itself when nothing replaces it; modded files come back as a local path,
    // possibly prefixed with |collection|.
    private static string? LocalFile(string? resolved)
    {
        if (string.IsNullOrEmpty(resolved)) return null;
        if (resolved[0] == '|')
        {
            var i = resolved.IndexOf('|', 1);
            if (i < 0) return null;
            resolved = resolved[(i + 1)..];
        }
        if (!Path.IsPathRooted(resolved)) return null;
        return File.Exists(resolved) ? resolved : null;
    }
}
