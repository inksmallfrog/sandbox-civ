using SimCore;

namespace CoreBasic;

/// <summary>
/// 国家的诞生与国库（GDD §4.1 的 <c>nation</c> 实体、§5.5 的「基建从国库出」）。
///
/// M1 只需要「有一个能被 <c>town.found{nation=…}</c> 引用、能扣钱的实体」，
/// 所以这里只落地三件事：建國、国库存粮、把都城指给一座城。
/// 行政瓶颈（§7.5）、税制（M4）、外交关系（M9）都不在这里，也不该在这里。
///
/// <para><b>设计偏离（已在 CHANGELOG-DESIGN 提请裁决）：</b>
/// GDD 把开局国家当作「场景」的一部分，但 §10 没有给场景数据文件留位置，
/// M1 于是先用命令建国 + 一个写死的开国库数，等 M2 有 <c>scenario</c> 数据包再换成数据驱动。</para>
/// </summary>
public static class Nations
{
    /// <summary>
    /// 开国库（粮）。占位值：只够付 6 座城的建城费，故意不给多，
    /// 让玩家第一年就撞上「国库见底 ⇒ 开不了新城」这条硬约束（§1.3 的代价看得见）。
    /// </summary>
    public static readonly Fix StartingTreasuryFood = Fix.FromInt(40);

    /// <summary><c>nation.found</c>：args = name（可省）、seed（可选：把已有的独立城镇收进来）。</summary>
    public static CommandResult Found(Command cmd, IStepContext ctx)
    {
        string name = cmd.ArgStr("name");
        if (string.IsNullOrEmpty(name)) name = "无名邦";

        EntityId seed = F.Id(cmd.Arg("seed"));
        if (!seed.IsEmpty && !ctx.Has(seed))
            return CommandResult.Reject(cmd, $"{seed} 不是已存在的城镇，不能当都城");
        if (!seed.IsEmpty && !F.IsBlank(ctx.GetStr(seed, F.TownOwner)))
            return CommandResult.Reject(cmd, $"{ctx.GetStr(seed, F.TownName)} 已附属 {ctx.GetStr(seed, F.TownOwner)}，不能被两个国家同时拥有");

        var nation = ctx.CreateEntity(F.KindNation, $"建国「{name}」（§4.1）");

        ctx.Mutate(nation.Id, F.NationName, name, "国名：命令给定，缺省为占位名");
        ctx.Mutate(nation.Id, F.NationPlayer, SimValue.OfBool(true), "§4.1 是否本机直接扮演；联机时由会话层的座位分配改写");
        ctx.Mutate(nation.Id, F.NationStockFood, StartingTreasuryFood, "§5.5：国库存粮，建城与营造从这一格扣");
        ctx.Mutate(nation.Id, F.NationCapital, seed.IsEmpty ? "" : seed.Value,
            seed.IsEmpty ? "M1：都城在首座城建成时由 UI 指认，先留空" : $"{seed} 为首都");

        if (!seed.IsEmpty)
        {
            ctx.Mutate(seed, F.TownOwner, nation.Id.Value, "都城归属");
            ctx.Mutate(seed, F.TownSovereignty, "core", "§4.1 治权：首都永远是 core 档");
        }

        ctx.Explain(nation.Id, "建国",
            "国库初值 = 占位常数（M2 换成 scenario 数据包）",
            [$"国名 {name}，实体 {nation.Id}",
             $"国库 {StartingTreasuryFood.Text} 石粮 ≈ 可付 {(long)(StartingTreasuryFood.Raw / TownOps.FoundCostFood.Raw)} 座城的建城费（{TownOps.FoundCostFood.Text} 石/座）",
             seed.IsEmpty ? "无都城：先用 town.found 建一座独立城镇，再由 UI 指认首都" : $"都城 = {seed}"],
            $"{name}（{nation.Id}）成立");
        ctx.Note($"建国：{name}（{nation.Id}），国库 {StartingTreasuryFood.Text} 石粮");
        return CommandResult.Accept(cmd, $"{name} 建国，国库 {StartingTreasuryFood.Text} 石", Fix.One);
    }

    /// <summary>
    /// 第 15 步的国库栏外，还需要一件小事：把「没有都城但境内有城」的国家补上首都。
    /// 挂 <c>OnTurnEnd</c>（第 15 步末），只在确实缺都城时才写，避免每年产生一笔无意义 diff。
    /// </summary>
    public static void AdoptCapital(HookArgs args, IStepContext ctx)
    {
        foreach (var nation in ctx.EntitiesOfKind(F.KindNation))
        {
            if (!F.IsBlank(ctx.GetStr(nation.Id, F.NationCapital))) continue;
            EntityId first = default;
            foreach (var town in ctx.EntitiesOfKind(F.KindTown))     // 已按 id 升序，取第一个即确定
            {
                if (F.IdOf(ctx.GetStr(town.Id, F.TownOwner)) != nation.Id) continue;
                first = town.Id;
                break;
            }
            if (first.IsEmpty) continue;
            // first 已由上面的 IsEmpty 保证非空，! 只是把这件事写给可空引用分析看
            ctx.Mutate(nation.Id, F.NationCapital, first.Value!, "自动指认：境内最早的城为首都（M1 无选址规则）");
            ctx.Mutate(first, F.TownSovereignty, "core", "首都治权升为 core（§4.1）");
            ctx.Note($"{ctx.GetStr(nation.Id, F.NationName)} 定都 {ctx.GetStr(first, F.TownName)}");
        }
    }
}
