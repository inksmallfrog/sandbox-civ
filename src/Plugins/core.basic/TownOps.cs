using SimCore;

namespace CoreBasic;

/// <summary>
/// 建城与营造（GDD §3.8 建城、§3.6 辖地、§4.1 城镇数据模型）。
///
/// 这两个命令是玩家最早碰到的东西，所以驳回理由必须说人话：
/// 「不行，并且告诉你为什么不行」是 §1.3 核心循环里「代价看得见」的一部分。
/// </summary>
public static class TownOps
{
    /// <summary>辖地半径与上限（§3.6：城镇把半径 2 内、最多 12 格绑为自己的辖地）。</summary>
    public const int ClaimRadius = 2;
    public const int ClaimMaxTiles = 12;

    /// <summary>
    /// 两城最小间距 = §3.8 的「距最近城镇 ≥ 3 格」，原值照抄，不是替身。
    /// 同一张表里的另一条「每国每 5 年最多新建 1 城」M1 <b>没做</b>：它要跨回合的国家命令配额，
    /// 而 M1 没有 AP（§6.3），做在 Core 里又违反「Core 无玩法」。缺口记在 docs/CHANGELOG-DESIGN.md。
    /// </summary>
    public const int MinTownSpacing = 3;

    /// <summary>建城成本（粮）。M1 没有价格系统，成本直接按实物计（§5.7 标定起点）。</summary>
    public static readonly Fix FoundCostFood = Fix.FromInt(6);

    /// <summary>首批人口：4 个百人单位 = 400 人（附录 D 的人口单位）。没有这批人，第一年产出恒为 0，玩家看不出循环有没有跑起来。</summary>
    public const long FoundPop = 4;

    /// <summary>开城仓底（粮）。</summary>
    public static readonly Fix FoundStockFood = Fix.FromInt(8);

    /// <summary>
    /// <c>town.found</c>：args = hex（地块或 <c>auto</c>）、name（可省）、nation（可省 = 独立城镇，§4.1；也可写 <c>auto</c> = 第一个国家）。
    ///
    /// <c>hex=auto</c> 是给脚本、标定与 AI 托管用的：命令必须能被<b>与种子无关</b>地写出来，
    /// 选址规则本身也是玩法知识，所以放在 <see cref="Siting"/> 里而不是壳层（§10.1）。
    /// </summary>
    public static CommandResult Found(Command cmd, IStepContext ctx)
    {
        EntityId hex = F.Id(cmd.Arg("hex"));
        if (Siting.IsAuto(hex))
        {
            hex = Siting.BestTownSite(ctx, out string why);
            if (hex.IsEmpty) return CommandResult.Reject(cmd, $"自动选址失败：{why}");
        }
        if (hex.IsEmpty) return CommandResult.Reject(cmd, "建城要指定地块：town.found{hex=…} 或 hex=auto");
        if (!ctx.Has(hex)) return CommandResult.Reject(cmd, $"{hex} 在地图上不存在");

        if (!ctx.Data.TryGet(ctx.GetStr(hex, F.HexLandform), out var landform))
            return CommandResult.Reject(cmd, $"{hex} 的地表记录 '{ctx.GetStr(hex, F.HexLandform)}' 找不到，无法判断可否建城");
        if (!landform.Get("buildable").AsBool())
            return CommandResult.Reject(cmd, $"{landform.GetStr("title")}不可建城（§3.2：山地只能做隘口，不是城址）");
        if (!F.IsBlank(ctx.GetStr(hex, F.HexTown)))
            return CommandResult.Reject(cmd, $"{hex} 已经有城：{ctx.GetStr(hex, F.HexTown)}");
        if (!F.IsBlank(ctx.GetStr(hex, F.HexOwner)))
            return CommandResult.Reject(cmd, $"{hex} 已是 {ctx.GetStr(hex, F.HexOwner)} 的辖地，辖地不重叠");

        EntityId nation = F.Id(cmd.Arg("nation"));
        if (Siting.IsAuto(nation)) nation = FirstEntityOf(ctx, F.KindNation);
        if (!nation.IsEmpty && !ctx.Has(nation))
            return CommandResult.Reject(cmd, $"{nation} 这个国家不存在");

        var (hq, hr) = MapGen.Coords(hex);
        foreach (var t in ctx.EntitiesOfKind(F.KindTown))
        {
            EntityId site = F.IdOf(ctx.GetStr(t.Id, F.TownSite));
            if (site.IsEmpty || !ctx.Has(site)) continue;   // 城址丢了就不比，交给不变量 basic.ranges 的「城址不存在/双向引用不一致」报出来
            var (q, r) = MapGen.Coords(site);
            int d = MapGen.Distance(q - hq, r - hr);
            if (d < MinTownSpacing)
                return CommandResult.Reject(cmd, $"离 {ctx.GetStr(t.Id, F.TownName)}（{t.Id}）只有 {d} 格，§3.8 的铺城限制要求至少隔 {MinTownSpacing} 格");
        }

        if (!nation.IsEmpty)
        {
            Fix treasury = ctx.GetFix(nation, F.NationStockFood, Fix.Zero);
            if (treasury < FoundCostFood)
                return CommandResult.Reject(cmd, $"{nation} 国库只有 {treasury.Text} 石粮，建城要 {FoundCostFood.Text} 石");
        }

        var town = ctx.CreateEntity(F.KindTown, $"在 {hex} 建城（§3.8）");
        string name = cmd.Arg("name").AsString();
        if (string.IsNullOrEmpty(name)) name = DefaultName(ctx, landform);

        ctx.Mutate(town.Id, F.TownName, name, "城名：命令给定，缺省按地表起底（不含随机字，回放必然一致）");
        ctx.Mutate(town.Id, F.TownOwner, nation.IsEmpty ? "" : nation.Value,
            nation.IsEmpty ? "§4.1/§6.2：owner 为空 = 独立城镇" : "归属国");
        ctx.Mutate(town.Id, F.TownSovereignty, nation.IsEmpty ? "independent" : "core",
            "§4.1 治权档位；M1 只写 core / independent，§6.2 的归属四态与 §6.3 的 AP 打折归 M9");
        ctx.Mutate(town.Id, F.TownTier, "village", "§4.1 规模档：village→town→city→metropolis，升级条件在 M4");
        ctx.Mutate(town.Id, F.TownFounded, ctx.Time.Turn, "建城回合：§3.8 的「每国每 5 年最多新建 1 城」与鬼城回收都要读它（M1 尚无读者）");
        ctx.Mutate(town.Id, F.TownFacility, Fix.One, "第 2 步会重算 Facility，这里先给中性值，免得建城当年读到空");
        ctx.Mutate(town.Id, F.TownSite, hex.Value, "城址：辖地半径、城间距、图层定位都以它为原点");

        ctx.Mutate(hex, F.HexTown, town.Id.Value, "城址格登记所属城镇");
        ctx.Mutate(hex, F.HexOwner, town.Id.Value, "城址本身也是辖地");
        int claimed = ClaimTiles(ctx, hex, town.Id);
        long tiles = claimed + 1L;                       // 城址 1 格 + 抢占的无主格
        ctx.Mutate(town.Id, F.TownTiles, tiles, $"辖地格数：城址 1 + 抢占 {claimed}（§3.6 上限 {ClaimMaxTiles}）");

        ctx.Mutate(town.Id, F.TownPop("class.peasant"), FoundPop, $"§3.8：建城即有首批人口 {FoundPop * 100} 人");
        ctx.Mutate(town.Id, F.TownSat("class.peasant"), Fix.FromInt(55), "§4.2 满意度初值：基线 50 加一点新开荒的期待");
        ctx.Mutate(town.Id, F.TownStock("good.food"), FoundStockFood, "开城仓底，§5.4「先有粮才有人」");
        ctx.Mutate(town.Id, F.TownGrowth("class.peasant"), Fix.Zero, "人口增长的零头累积：不到 100 人不进位");

        if (!nation.IsEmpty)
            ctx.Add(nation, F.NationStockFood, SimValue.Of(-FoundCostFood), $"§3.8 的建城成本，由 §5.5 的国库支付：{FoundCostFood.Text} 石粮");

        ctx.Explain(town.Id, "建城",
            $"辖地 = 半径 {ClaimRadius} 内至多 {ClaimMaxTiles} 格（§3.6）",
            [$"城址 {hex}，地表 {landform.GetStr("title")}",
             $"抢占无主格 {claimed} 格（已属他城的不抢）",
             $"首批 {FoundPop} 个百人单位 = {FoundPop * 100} 人，仓底 {FoundStockFood.Text} 石",
             $"国库支出 {FoundCostFood.Text} 石粮"],
            $"{name}（{town.Id}）建在 {hex}，辖地 {tiles} 格");
        ctx.Note($"建城：{name} @ {hex}（{landform.GetStr("title")}），辖地 {tiles} 格");
        // ExecutionRate = 1.00：M1 没有行政瓶颈（§7.5），上令即下达。M5 起这里会打折。
        return CommandResult.Accept(cmd, $"{name} 建城完成，辖地 {tiles} 格", Fix.One);
    }

    /// <summary>
    /// <c>building.build</c>：args = town（或 <c>auto</c> = 第一座城）、building（<c>def:</c> 或裸 id）、
    /// hex（地块类建筑必填，也可写 <c>auto</c> 让规则包在辖地里选址）。
    /// 营造本身不进结算——它只改等级，第 2 步负责把等级折算成设施系数（§2.3 的分工）。
    /// </summary>
    public static CommandResult Build(Command cmd, IStepContext ctx)
    {
        EntityId town = F.Id(cmd.Arg("town"));
        if (Siting.IsAuto(town)) town = FirstEntityOf(ctx, F.KindTown);
        if (town.IsEmpty || !ctx.Has(town)) return CommandResult.Reject(cmd, "building.build 需要一个存在的 town（或 town=auto）");

        string defId = F.BareId(cmd.ArgStr("building"));
        if (defId.Length == 0 || !ctx.Data.TryGet(defId, out var def))
            return CommandResult.Reject(cmd, $"营造目标 '{cmd.ArgStr("building")}' 不是已知建筑（type=building）");
        if (def.Type != F.TypeBuilding)
            return CommandResult.Reject(cmd, $"{defId} 是 {def.Type}，不是建筑");

        bool onHex = def.GetStr("slot") == "hex";
        EntityId hex = default;
        if (onHex)
        {
            hex = F.Id(cmd.Arg("hex"));
            if (Siting.IsAuto(hex)) hex = Siting.BestBuildingHex(ctx, town, def.Id);
            if (hex.IsEmpty || !ctx.Has(hex))
                return CommandResult.Reject(cmd, $"{def.GetStr("title")} 盖在地块上，必须给 hex（或 hex=auto 让规则包在辖地里挑一格）");
            if (F.IdOf(ctx.GetStr(hex, F.HexOwner)) != town)
                return CommandResult.Reject(cmd, $"{hex} 不在 {ctx.GetStr(town, F.TownName)} 的辖地里");
        }

        EntityId where = onHex ? hex : town;
        string path = onHex ? F.HexBuilding(defId) : F.TownBuilding(defId);
        long level = ctx.GetInt(where, path);
        int max = (int)def.GetInt("maxLevel", 1);
        if (level >= max) return CommandResult.Reject(cmd, $"{def.GetStr("title")} 已经 {level} 级，升到顶了（上限 {max}）");

        Fix cost = level == 0 ? def.GetFix("cost.food") : def.GetFix("upgradeCost.food", def.GetFix("cost.food"));
        Fix stock = ctx.GetFix(town, F.TownStock("good.food"), Fix.Zero);
        if (stock < cost)
            return CommandResult.Reject(cmd, $"{def.GetStr("title")} 要 {cost.Text} 石粮，仓里只有 {stock.Text} 石");

        long reqPop = def.GetInt("requiresPop", 0);
        long pop = TotalPop(ctx, town);
        if (reqPop > 0 && pop < reqPop)
            return CommandResult.Reject(cmd, $"{def.GetStr("title")} 需要 {reqPop} 个百人单位，现在只有 {pop} 个（§4.4 的解锁门槛）");

        ctx.Add(where, path, SimValue.OfInt(1), $"营造：{def.GetStr("title")} 升到 {level + 1} 级");
        ctx.Add(town, F.TownStock("good.food"), SimValue.Of(-cost), $"§5.5 基建：扣营造费 {cost.Text} 石");
        ctx.Explain(town, $"{def.GetStr("title")}（{(onHex ? "地块" : "城镇")}设施）",
            "Facility = 1 + Σ(每级加成 × 等级)，上限 1.45（§4.2）",
            [$"{def.GetStr("title")} 等级 {level} → {level + 1}{(onHex ? $" @ {hex}" : "")}",
             $"每级加成 {def.GetFix("facility").Text}，本次花费 {(level == 0 ? "首建 " : "升级 ") + cost.Text} 石粮",
             $"仓底 {stock.Text} → {(stock - cost).Text} 石"],
            "第 2 步会把新等级折进 Facility，第 3 步的产出上限因此变化");
        ctx.Note($"营造：{ctx.GetStr(town, F.TownName)} 的 {def.GetStr("title")} → {level + 1} 级（-{cost.Text} 石）");
        return CommandResult.Accept(cmd, $"{def.GetStr("title")} 升到 {level + 1} 级", Fix.One);
    }

    /// <summary>
    /// 第 2 步「建筑与设施生效/完工」：把等级表折成 Facility、仓容与损耗率。
    /// 它必须排在第 3 步产出之前（§2.3），否则「今年新建的田」会凭空多出今年的粮。
    ///
    /// 位置闸门的写法有个坑：<c>OnProduction</c> 同时是第 2 步的 post 和第 3–5 步的 pre/post，
    /// 只判 hook 点会跑五遍，所以判到 (stepId, slot) 这一级（见 <see cref="SimOrder.At"/>）。
    /// </summary>
    public static void Facilities(HookArgs args, IStepContext ctx)
    {
        if (!SimOrder.At(args, "buildings", SimOrder.SlotPost)) return;

        var defs = ctx.Data.OfType(F.TypeBuilding);
        foreach (var town in ctx.EntitiesOfKind(F.KindTown))
        {
            var tiles = TilesOf(ctx, town.Id);
            Fix facilitySum = Fix.Zero;
            Fix stockCapBonus = Fix.Zero;
            Fix spoilageCut = Fix.Zero;

            foreach (var def in defs)
            {
                if (def.GetStr("slot") == "hex")
                {
                    long levels = 0;
                    foreach (var t in tiles) levels += ctx.GetInt(t, F.HexBuilding(def.Id));
                    facilitySum += Fix.Mul(def.GetFix("facility"), levels);
                }
                else
                {
                    long lvl = ctx.GetInt(town.Id, F.TownBuilding(def.Id));
                    facilitySum += Fix.Mul(def.GetFix("facility"), lvl);
                    stockCapBonus += Fix.Mul(def.GetFix("stockCap"), lvl);
                    spoilageCut += Fix.Mul(def.GetFix("spoilageCut"), lvl);
                }
            }

            // §4.2：Facility 上限 1.45。设施堆到顶以后再多盖也不涨，这是「边际设施递减」的第一道闸。
            Fix facility = Fix.One + Fix.Min(facilitySum, Fix.FromPercent(45));
            ctx.Mutate(town.Id, F.TownFacility, facility,
                $"Facility = 1 + Σ(每级加成 × 等级) = 1 + {facilitySum.Text}（§4.2 封顶 1.45）→ {facility.Text}");

            Fix grain = ctx.Data.Get("good.food").GetFix("stockCapPerPop");
            Fix cap = Fix.Mul(grain, TotalPop(ctx, town.Id)) + stockCapBonus;
            ctx.Mutate(town.Id, F.TownStockCap("good.food"), cap,
                $"仓容 = 人口 × {grain.Text} + 粮仓加成 {stockCapBonus.Text}（§5.4 步 6：剩余物资留仓，仓满即抛）");

            Fix spoil = ctx.Data.Get("good.food").GetFix("spoilage") - spoilageCut;
            ctx.Mutate(town.Id, F.TownSpoilage("good.food"), Fix.Max(spoil, Fix.Zero),
                $"粮损耗 = {ctx.Data.Get("good.food").GetFix("spoilage").Text} − 粮仓每级 {spoilageCut.Text}（§5.7）");
        }
    }

    // ---- 工具 ----

    /// <summary>某 kind 的第一个实体 id（<c>auto</c> 参数的落点）。EntitiesOfKind 已按 id 升序，所以结果确定。</summary>
    private static EntityId FirstEntityOf(IStepContext ctx, string kind)
    {
        foreach (var e in ctx.EntitiesOfKind(kind)) return e.Id;
        return default;
    }

    /// <summary>辖地抢占：按（离城址的距离，地块 id）升序取前 N 个无主格。排序键唯一 ⇒ 结果唯一（§10.7 纪律 1）。</summary>
    private static int ClaimTiles(IStepContext ctx, EntityId center, EntityId town)
    {
        var (cq, cr) = MapGen.Coords(center);
        var candidates = new List<(int dist, string id)>();
        foreach (var hex in ctx.EntitiesOfKind(F.KindHex))
        {
            if (!F.IsBlank(ctx.GetStr(hex.Id, F.HexOwner))) continue;      // 已属他城，不抢
            var (q, r) = MapGen.Coords(hex.Id);
            int d = MapGen.Distance(q - cq, r - cr);
            if (d < 1 || d > ClaimRadius) continue;
            candidates.Add((d, hex.Id.Value));
        }

        int claimed = 0;
        // 上限按「辖地总格数」算，城址自己占 1 格，所以最多再抢 ClaimMaxTiles − 1
        foreach (var id in candidates.OrderBy(x => x.dist).ThenBy(x => x.id, StringComparer.Ordinal)
                     .Take(ClaimMaxTiles - 1).Select(x => x.id))
        {
            ctx.Mutate(new EntityId(id), F.HexOwner, town.Value, $"§3.6：{town} 建城时划入辖地（半径 {ClaimRadius}，上限 {ClaimMaxTiles}）");
            claimed++;
        }
        return claimed;
    }

    /// <summary>某城的辖地格（含城址），按 id 升序。调用方的累加顺序因此确定。</summary>
    public static List<EntityId> TilesOf(IStepContext ctx, EntityId town)
    {
        var list = new List<EntityId>();
        foreach (var hex in ctx.EntitiesOfKind(F.KindHex))
            if (F.IdOf(ctx.GetStr(hex.Id, F.HexOwner)) == town) list.Add(hex.Id);
        list.Sort();
        return list;
    }

    /// <summary>城镇总人口（百人单位）。遍历 pop.* 前缀，阶级加了也不用改这里。</summary>
    public static long TotalPop(IStepContext ctx, EntityId town)
    {
        long sum = 0;
        foreach (var kv in ctx.World.Get(town)!.Prefix("town.pop.")) sum += kv.Value.AsInt();
        return sum;
    }

    private static string DefaultName(IStepContext ctx, DataEntry landform)
    {
        int n = ctx.EntitiesOfKind(F.KindTown).Count;
        return n == 0 ? $"{landform.GetStr("title")}城" : $"{landform.GetStr("title")}城{n + 1}";
    }
}
