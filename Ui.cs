using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Windowing;

namespace EmoteMirror;

public sealed class MainWindow : Window
{
    private readonly Plugin p;

    public MainWindow(Plugin plugin) : base("Emote Mirror###EmoteMirrorMain")
    {
        p = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(380, 260), MaximumSize = new Vector2(900, 1200) };
        Size = new Vector2(420, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw() => p.DrawUi();
}

public sealed partial class Plugin
{
    private readonly WindowSystem windows = new("EmoteMirror");
    private MainWindow? mainWindow;
    private string blackInput = string.Empty, whiteInput = string.Empty;

    private void InitUi()
    {
        mainWindow = new MainWindow(this);
        windows.AddWindow(mainWindow);
        Pi.UiBuilder.Draw += windows.Draw;
        Pi.UiBuilder.OpenMainUi += ToggleUi;
        Pi.UiBuilder.OpenConfigUi += ToggleUi;
    }

    private void DisposeUi()
    {
        Pi.UiBuilder.Draw -= windows.Draw;
        Pi.UiBuilder.OpenMainUi -= ToggleUi;
        Pi.UiBuilder.OpenConfigUi -= ToggleUi;
        windows.RemoveAllWindows();
    }

    private void ToggleUi() => mainWindow!.Toggle();

    internal void DrawUi()
    {
        // ---- emotes
        if (ImGui.CollapsingHeader("Эмоции", ImGuiTreeNodeFlags.DefaultOpen))
        {
            bool changed = false;
            var en = cfg.Enabled;
            if (ImGui.Checkbox("Копировать мод эмоции, которую кидают в меня", ref en)) { cfg.Enabled = en; changed = true; if (!en) Clear(); }
            var rp = cfg.ReplyMyself;
            if (ImGui.Checkbox("Самостоятельно отвечать эмоцией", ref rp)) { cfg.ReplyMyself = rp; changed = true; }
            var friends = cfg.OnlyFriends;
            if (ImGui.Checkbox("Применять только к игрокам из списка друзей", ref friends)) { cfg.OnlyFriends = friends; changed = true; }
            var party = cfg.OnlyParty;
            if (ImGui.Checkbox("Применять только к участникам группы (и кросс-ворлд)", ref party)) { cfg.OnlyParty = party; changed = true; }
            var syncFriends = cfg.SyncFriends;
            if (ImGui.Checkbox("Синхронизировать список друзей вне данжей", ref syncFriends)) { cfg.SyncFriends = syncFriends; changed = true; }
            var mute = cfg.MuteCopiedSound;
            if (ImGui.Checkbox("Копировать эмоцию без звука", ref mute)) { cfg.MuteCopiedSound = mute; changed = true; if (appliedTo != Guid.Empty) Apply(); }
            var openWithEmotes = cfg.OpenWithEmoteWindow;
            if (ImGui.Checkbox("Открывать окно Emote Mirror вместе с окном эмоций", ref openWithEmotes)) { cfg.OpenWithEmoteWindow = openWithEmotes; changed = true; }
            var dbg = cfg.Debug;
            if (ImGui.Checkbox("Отладка в чат", ref dbg)) { cfg.Debug = dbg; changed = true; }
            if (changed) Save();
            if (appliedTo != Guid.Empty)
            {
                var who = Objects[sourceIndex]?.Name.TextValue ?? "?";
                ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1), $"Сейчас повторяю: {who}, файлов {paths.Count}");
                ImGui.SameLine();
                if (ImGui.SmallButton("Снять")) Clear();
            }
            else ImGui.TextDisabled("Сейчас ничего не повторяю");
        }

        // ---- white / black lists
        if (ImGui.CollapsingHeader("Белый список", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var ow = cfg.OnlyWhitelist;
            if (ImGui.Checkbox("Копировать только у игроков из белого списка", ref ow))
            {
                cfg.OnlyWhitelist = ow; Save();
                if (ow && appliedTo != Guid.Empty && Objects[sourceIndex] is IPlayerCharacter src && src.Address == sourceAddr && !Allowed(src, out _)) Clear();
            }
            ImGui.TextDisabled("Эти игроки проходят всегда, даже если не друзья и не в группе.");
            DrawList(White, ref whiteInput);
        }
        if (ImGui.CollapsingHeader("Чёрный список", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextDisabled("Эмоции этих игроков не копируются никогда.");
            DrawList(Black, ref blackInput);
        }
    }

    private void DrawList(NameList l, ref string input)
    {
        ImGui.PushID(l.Title);
        ImGui.SetNextItemWidth(-130);
        var enter = ImGui.InputTextWithHint("##name", "Имя Фамилия[@Мир]", ref input, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Добавить") || enter) && input.Trim().Length > 0)
        {
            if (Add(l, input, false)) input = string.Empty;
        }
        ImGui.SameLine();
        var target = Targets.Target as IPlayerCharacter;
        var noTarget = target == null || target.Address == Objects.LocalPlayer?.Address;
        if (noTarget) ImGui.BeginDisabled();
        if (ImGui.Button("Таргет") && target != null) Add(l, Entry(target), false);
        if (noTarget) ImGui.EndDisabled();

        var list = l.Get(cfg);
        if (list.Count == 0) ImGui.TextDisabled("Пусто");
        string? remove = null;
        foreach (var e in list)
        {
            ImGui.PushID(e);
            if (ImGui.SmallButton("x")) remove = e;
            ImGui.SameLine();
            ImGui.TextUnformatted(e);
            ImGui.PopID();
        }
        if (remove != null) Remove(l, remove, false);
        ImGui.PopID();
    }
}
