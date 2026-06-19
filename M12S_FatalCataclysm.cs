using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KodakkuAssist.Extensions;
using KodakkuAssist.Module.Draw;
using KodakkuAssist.Module.GameEvent;
using KodakkuAssist.Script;
using Newtonsoft.Json;

namespace KarlinScriptNamespace;

[ScriptType(name: "M12S 致命灾变绘图", territorys: [1327], guid: "1e7c0b5f-7b27-4de6-9a3b-6c0d9d8d1c45", version: "0.0.0.2", author: "Codex")]
public class M12SFatalCataclysm
{
    private static readonly Vector3 BossCenter = new(100f, 0f, 85f);

    // 1 < 2 < 3 < 4 by distance from boss target-circle center.
    private static readonly Vector3[] LeftSpots =
    [
        new(95.0f, 0f, 86.0f),
        new(96.2f, 0f, 91.4f),
        new(92.3f, 0f, 91.4f),
        new(86.5f, 0f, 86.0f),
    ];

    private static readonly Vector3[] RightSpots =
    [
        new(105.0f, 0f, 86.0f),
        new(103.8f, 0f, 91.4f),
        new(107.7f, 0f, 91.4f),
        new(113.5f, 0f, 86.0f),
    ];

    private readonly object fatalLock = new();
    private readonly List<FatalOrb> fatalOrbs = [];
    private bool fatalActive;
    private bool fatalResolved;
    private DateTime fatalStartedAt;

    [UserSetting("显示全部处理点")]
    public bool DrawAllSpots { get; set; } = true;

    [UserSetting("显示判定圈")]
    public bool DrawAoeCircles { get; set; } = true;

    [UserSetting("调试信息")]
    public bool DebugMode { get; set; } = false;

    public void Init(ScriptAccessory accessory)
    {
        ResetFatal();
    }

    [ScriptMethod(name: "致命灾变-开始收集", eventType: EventTypeEnum.StartCasting, eventCondition: ["ActionId:46229"], userControl: false)]
    public void FatalCataclysmStart(Event @event, ScriptAccessory accessory)
    {
        ResetFatal();
        fatalActive = true;
        fatalStartedAt = DateTime.Now;

        if (DebugMode)
            accessory.Method.SendChat("/e [M12S] 致命灾变：开始收集 8 个球。");
    }

    [ScriptMethod(name: "致命灾变-绿球收集", eventType: EventTypeEnum.AddCombatant, eventCondition: ["DataId:19201"], userControl: false)]
    public void FatalCataclysmGreenOrb(Event @event, ScriptAccessory accessory)
    {
        CollectFatalOrb(@event, accessory, FatalOrbKind.Green);
    }

    [ScriptMethod(name: "致命灾变-紫球收集", eventType: EventTypeEnum.AddCombatant, eventCondition: ["DataId:19200"], userControl: false)]
    public void FatalCataclysmPurpleOrb(Event @event, ScriptAccessory accessory)
    {
        CollectFatalOrb(@event, accessory, FatalOrbKind.Purple);
    }

    private void CollectFatalOrb(Event @event, ScriptAccessory accessory, FatalOrbKind kind)
    {
        lock (fatalLock)
        {
            if (!fatalActive || fatalResolved) return;
            if ((DateTime.Now - fatalStartedAt).TotalSeconds > 25) return;
            if (!ParseObjectId(@event["SourceId"], out var sourceId)) return;
            if (!TryGetSourcePosition(@event, accessory, sourceId, out var pos)) return;

            var side = pos.X < BossCenter.X ? FatalSide.Left : FatalSide.Right;
            var round = fatalOrbs.Count / 2;

            fatalOrbs.Add(new FatalOrb(sourceId, kind, side, round, pos));

            if (DebugMode)
                accessory.Method.SendChat($"/e [M12S] 致命灾变球 {fatalOrbs.Count}/8: {SideName(side)} {KindName(kind)} ({pos.X:F1},{pos.Z:F1})");

            if (fatalOrbs.Count < 8) return;

            fatalResolved = true;
            DrawFatalSolution(accessory);
        }
    }

    private void DrawFatalSolution(ScriptAccessory accessory)
    {
        var leftPurple = fatalOrbs.Count(o => o.Side == FatalSide.Left && o.Kind == FatalOrbKind.Purple);
        var rightPurple = fatalOrbs.Count(o => o.Side == FatalSide.Right && o.Kind == FatalOrbKind.Purple);

        if (leftPurple == rightPurple)
        {
            if (DebugMode)
                accessory.Method.SendChat($"/e [M12S] 致命灾变：紫球分布异常，左{leftPurple}右{rightPurple}。");
            return;
        }

        var supportSide = leftPurple == 2 ? FatalSide.Left : FatalSide.Right;
        var dpsSide = supportSide == FatalSide.Left ? FatalSide.Right : FatalSide.Left;

        var supportSequence = GetSideSequence(supportSide);
        var dpsSequence = GetSideSequence(dpsSide);

        if (supportSequence.Count != 4 || dpsSequence.Count != 4)
        {
            if (DebugMode)
                accessory.Method.SendChat($"/e [M12S] 致命灾变：球数量异常，支援侧{supportSequence.Count}，DPS侧{dpsSequence.Count}。");
            return;
        }

        var assignments = BuildAssignments(supportSequence);
        for (var i = 0; i < 4; i++)
            assignments[4 + i] = i;

        if (DrawAllSpots)
        {
            DrawSideSpots(accessory, supportSide, supportSequence, "支援");
            DrawSideSpots(accessory, dpsSide, dpsSequence, "DPS");
        }

        if (DrawAoeCircles)
        {
            DrawTimedAoeCircles(accessory, supportSide, supportSequence);
            DrawTimedAoeCircles(accessory, dpsSide, dpsSequence);
        }

        var myIndex = accessory.Data.PartyList.IndexOf(accessory.Data.Me);
        if (myIndex < 0 || myIndex > 7) return;
        if (!assignments.TryGetValue(myIndex, out var mySpotIndex)) return;

        var mySide = myIndex < 4 ? supportSide : dpsSide;
        var myPos = GetSpot(mySide, mySpotIndex);

        DrawPersonalGuide(accessory, myPos, mySide, mySpotIndex, myIndex);

        if (DebugMode)
        {
            var supportText = string.Join("", supportSequence.Select(o => o.Kind == FatalOrbKind.Purple ? "紫" : "绿"));
            var dpsText = string.Join("", dpsSequence.Select(o => o.Kind == FatalOrbKind.Purple ? "紫" : "绿"));
            accessory.Method.SendChat($"/e [M12S] 致命灾变：{SideName(supportSide)}侧支援 {supportText}，{SideName(dpsSide)}侧DPS {dpsText}。你去{SideName(mySide)}{mySpotIndex + 1}。");
        }
    }

    private static Dictionary<int, int> BuildAssignments(IReadOnlyList<FatalOrb> supportSequence)
    {
        var assignments = new Dictionary<int, int>();
        var tankOrder = new[] { 0, 1 }; // MT, ST
        var healerOrder = new[] { 2, 3 }; // H1, H2
        var tankIndex = 0;
        var healerIndex = 0;

        for (var i = 0; i < supportSequence.Count; i++)
        {
            var orb = supportSequence[i];
            if (orb.Kind == FatalOrbKind.Purple)
            {
                if (tankIndex < tankOrder.Length)
                    assignments[tankOrder[tankIndex++]] = i;
            }
            else
            {
                if (healerIndex < healerOrder.Length)
                    assignments[healerOrder[healerIndex++]] = i;
            }
        }

        return assignments;
    }

    private List<FatalOrb> GetSideSequence(FatalSide side)
    {
        return fatalOrbs
            .Where(o => o.Side == side)
            .GroupBy(o => o.Round)
            .OrderBy(g => g.Key)
            .SelectMany(OrderRoundOrbs)
            .ToList();
    }

    private static IEnumerable<FatalOrb> OrderRoundOrbs(IGrouping<int, FatalOrb> group)
    {
        var list = group.ToList();
        if (list.Count <= 1)
        {
            return list;
        }

        return list.OrderByDescending(o => MathF.Abs(o.Position.X - BossCenter.X));
    }

    private static Vector3 GetSpot(FatalSide side, int index)
    {
        index = Math.Clamp(index, 0, 3);
        return side == FatalSide.Left ? LeftSpots[index] : RightSpots[index];
    }

    private void DrawSideSpots(ScriptAccessory accessory, FatalSide side, IReadOnlyList<FatalOrb> sequence, string groupName)
    {
        for (var i = 0; i < sequence.Count; i++)
        {
            var pos = GetSpot(side, i);
            var dp = accessory.Data.GetDefaultDrawProperties();
            dp.Name = $"致命灾变-{groupName}-{SideName(side)}{i + 1}-{KindName(sequence[i].Kind)}";
            dp.Scale = new(0.75f);
            dp.Position = pos;
            dp.Color = sequence[i].Kind == FatalOrbKind.Purple
                ? accessory.Data.DefaultDangerColor
                : accessory.Data.DefaultSafeColor;
            dp.DestoryAt = 17000;
            accessory.Method.SendDraw(DrawModeEnum.Default, DrawTypeEnum.Circle, dp);
        }
    }

    private void DrawTimedAoeCircles(ScriptAccessory accessory, FatalSide side, IReadOnlyList<FatalOrb> sequence)
    {
        var elapsed = (int)(DateTime.Now - fatalStartedAt).TotalMilliseconds;

        for (var i = 0; i < sequence.Count; i++)
        {
            var dp = accessory.Data.GetDefaultDrawProperties();
            dp.Name = $"致命灾变-判定圈-{SideName(side)}{i + 1}";
            dp.Scale = new(5.0f);
            dp.Position = GetSpot(side, i);
            dp.Color = accessory.Data.DefaultDangerColor;
            dp.Delay = Math.Max(0, 12000 + i * 3000 - elapsed);
            dp.DestoryAt = 2200;
            accessory.Method.SendDraw(DrawModeEnum.Default, DrawTypeEnum.Circle, dp);
        }
    }

    private static void DrawPersonalGuide(ScriptAccessory accessory, Vector3 target, FatalSide side, int spotIndex, int partyIndex)
    {
        var roleName = RoleName(partyIndex);

        var dp = accessory.Data.GetDefaultDrawProperties();
        dp.Name = $"致命灾变-个人引导-{roleName}-{SideName(side)}{spotIndex + 1}";
        dp.Scale = new(2.0f);
        dp.Owner = accessory.Data.Me;
        dp.TargetPosition = target;
        dp.ScaleMode |= ScaleMode.YByDistance;
        dp.Color = accessory.Data.DefaultSafeColor;
        dp.DestoryAt = 17000;
        accessory.Method.SendDraw(DrawModeEnum.Imgui, DrawTypeEnum.Displacement, dp);

        dp = accessory.Data.GetDefaultDrawProperties();
        dp.Name = $"致命灾变-个人站位-{roleName}-{SideName(side)}{spotIndex + 1}";
        dp.Scale = new(1.2f);
        dp.Position = target;
        dp.Color = accessory.Data.DefaultSafeColor;
        dp.DestoryAt = 17000;
        accessory.Method.SendDraw(DrawModeEnum.Default, DrawTypeEnum.Circle, dp);
    }

    private void ResetFatal()
    {
        lock (fatalLock)
        {
            fatalOrbs.Clear();
            fatalActive = false;
            fatalResolved = false;
            fatalStartedAt = DateTime.MinValue;
        }
    }

    private static string SideName(FatalSide side) => side == FatalSide.Left ? "左" : "右";

    private static string KindName(FatalOrbKind kind) => kind == FatalOrbKind.Purple ? "紫" : "绿";

    private static string RoleName(int index) => index switch
    {
        0 => "MT",
        1 => "ST",
        2 => "H1",
        3 => "H2",
        4 => "D1",
        5 => "D2",
        6 => "D3",
        7 => "D4",
        _ => "未知",
    };

    private static bool ParseObjectId(string? idStr, out uint id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(idStr)) return false;

        try
        {
            var clean = idStr.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
            id = uint.Parse(clean, System.Globalization.NumberStyles.HexNumber);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetSourcePosition(Event @event, ScriptAccessory accessory, uint sourceId, out Vector3 position)
    {
        position = default;

        try
        {
            var json = @event["SourcePosition"];
            if (!string.IsNullOrWhiteSpace(json))
            {
                position = JsonConvert.DeserializeObject<Vector3>(json);
                if (position != default)
                    return true;
            }
        }
        catch
        {
            // Some event providers omit SourcePosition on AddCombatant; fall back to the object table.
        }

        var obj = accessory.Data.Objects.SearchByEntityId(sourceId);
        if (obj == null) return false;

        position = obj.Position;
        return true;
    }

    private enum FatalSide
    {
        Left,
        Right,
    }

    private enum FatalOrbKind
    {
        Green,
        Purple,
    }

    private sealed record FatalOrb(uint SourceId, FatalOrbKind Kind, FatalSide Side, int Round, Vector3 Position);
}
