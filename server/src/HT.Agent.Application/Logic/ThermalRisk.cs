namespace HT.Agent.Application.Logic;

/// <summary>反应热安全评估：绝热温升、MTSR，按 Stoessel 方法分 1–5 级。组件里的数由系统算，不用模型写的。
/// Tp 工艺温度；MTSR 冷却失效后反应能到的最高温度 = Tp + 累积度 × 绝热温升；
/// MTT 技术上的最高温度（敞口体系是沸点，密闭体系是设备允许的最高温度、或泄放设定对应的温度）；
/// TD24 物料分解反应 24 小时达到最大速率的温度（要 DSC / ARC 实测）。</summary>
public static class ThermalRisk
{
    /// <summary>绝热温升（K）。给了单位质量反应热 q（kJ/kg）就直接 q / cp；否则 |ΔH|(kJ/mol) × c(mol/L) / (ρ(kg/L) × cp(kJ/(kg·K)))。</summary>
    public static double? AdiabaticRise(double? dHkJmol, double? concMolL, double? densityKgL, double? cpKJkgK, double? qKJkg = null)
    {
        if (cpKJkgK is not > 0) return null;
        if (qKJkg is { } q) return Math.Abs(q) / cpKJkgK.Value;
        if (dHkJmol is not { } dh || concMolL is not > 0 || densityKgL is not > 0) return null;
        return Math.Abs(dh) * concMolL.Value / (densityKgL.Value * cpKJkgK.Value);
    }

    /// <summary>MTSR：累积度 accumulation 取 0–1（一次全投就是 1）。</summary>
    public static double Mtsr(double tp, double dTad, double accumulation = 1) => tp + Math.Clamp(accumulation, 0, 1) * dTad;

    /// <summary>Stoessel 危险度 1–5 级；MTT 或 TD24 不知道就给不出（null）。
    /// 1：MTSR &lt; MTT &lt; TD24　2：MTSR &lt; TD24 &lt; MTT　3：MTT &lt; MTSR &lt; TD24　4：MTT &lt; TD24 &lt; MTSR　5：TD24 &lt; MTSR，且 TD24 &lt; MTT。</summary>
    public static int? Class(double mtsr, double? mtt, double? td24)
    {
        if (mtt is not { } m || td24 is not { } d) return null;
        if (mtsr < m) return mtsr < d ? (m < d ? 1 : 2) : 5;
        if (mtsr < d) return 3;
        return m < d ? 4 : 5;
    }

    public static string Describe(int? cls) => cls switch
    {
        1 => "1 级：冷却失效也到不了 MTT，MTT 又低于分解温度，风险低；常规温度控制和超温报警即可",
        2 => "2 级：冷却失效到不了分解温度；但 MTT 高于 TD24，物料在高温下久留（如反应停了还在加热）会引发分解，要避免高温久留",
        3 => "3 级：冷却失效会超过 MTT（溶剂沸腾或到泄放压力），靠沸腾冷却或泄放兜住，到不了分解温度；要核算冷凝和泄放能力",
        4 => "4 级：冷却失效会超过 MTT，沸腾冷却或泄放兜不住就会引发分解；要可靠的紧急冷却、泄放，温度与加料联锁",
        5 => "5 级：冷却失效会在到 MTT 之前就引发分解，沸腾冷却和泄放都兜不住；要改工艺（滴加、降低累积度、稀释）再评估",
        _ => "缺 MTT 或 TD24，分不了级",
    };
}
