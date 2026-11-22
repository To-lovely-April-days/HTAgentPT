namespace HT.Agent.Domain.Entities;

/// <summary>重点监管危险化工工艺（应急管理部公布的 18 类，2013 年完整版）。系统自带，后台可以改；改过的不会被系统更新覆盖。
/// 全公司共用一份（是国家的目录，不分公司）。</summary>
public class HazardProcess
{
    public Guid Id { get; set; }
    /// <summary>目录里的序号 1–18。</summary>
    public int No { get; set; }
    public required string Name { get; set; }
    public string ReactionType { get; set; } = "";
    public string MonitorUnits { get; set; } = "";
    public string Intro { get; set; } = "";
    public string Hazards { get; set; } = "";
    /// <summary>典型工艺（原文分行）。</summary>
    public string Typical { get; set; } = "";
    /// <summary>重点监控工艺参数。</summary>
    public string Params { get; set; } = "";
    /// <summary>安全控制的基本要求。</summary>
    public string Requirements { get; set; } = "";
    /// <summary>宜采用的控制方式。</summary>
    public string Controls { get; set; } = "";
    public required string Source { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>后台改过的人；为空 = 系统自带原样。</summary>
    public Guid? UpdatedById { get; set; }
}

/// <summary>《危险化学品目录（2015 版）》的一条，带分类信息表里的危险性类别。一个序号下可能有几条（不同浓度、溶液）。</summary>
public class HazardChemical
{
    public Guid Id { get; set; }
    /// <summary>目录序号 1–2828。</summary>
    public int Seq { get; set; }
    /// <summary>同一序号下的细分条目（如某某溶液）。</summary>
    public bool Sub { get; set; }
    public required string Name { get; set; }
    /// <summary>别名，用「；」隔开。</summary>
    public string Aliases { get; set; } = "";
    public string? English { get; set; }
    public string? Cas { get; set; }
    /// <summary>危险性类别，一行一个（如「易燃液体，类别2」）。</summary>
    public string Classes { get; set; } = "";
    /// <summary>剧毒化学品。</summary>
    public bool Toxic { get; set; }
    /// <summary>调整说明（如 2022 年柴油条目调整）。</summary>
    public string? Note { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedById { get; set; }
}

/// <summary>易制爆（公安部 2017 年版名录）、易制毒（条例附表及增补）化学品。</summary>
public class ControlledChemical
{
    public Guid Id { get; set; }
    /// <summary>yzb 易制爆 / yzd 易制毒。</summary>
    public required string List { get; set; }
    /// <summary>名录里的编号（如 1.1、第二类-3）。</summary>
    public required string No { get; set; }
    public required string Name { get; set; }
    public string? Cas { get; set; }
    /// <summary>易制毒的类别（第一类 / 第二类 / 第三类）。</summary>
    public string? Category { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedById { get; set; }
}
