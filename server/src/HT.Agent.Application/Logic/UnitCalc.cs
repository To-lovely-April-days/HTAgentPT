using System.Globalization;
using System.Text.RegularExpressions;

namespace HT.Agent.Application.Logic;

/// <summary>助手的计算工具：四则运算和常用工程单位换算。模型心算容易错（尤其 mbar 和 MPa 差四个数量级），
/// 数字一律交给这里算。只做通用数学和单位，不含任何业务规则。</summary>
public static class UnitCalc
{
    // ── 四则运算 ─────────────────────────────────────────
    /// <summary>算一个表达式：+ - * / ^ 括号、小数、科学计数法、sqrt/abs/ln/log10/exp/round/min/max/pi。</summary>
    public static double Evaluate(string expression)
    {
        var p = new Parser(Normalize(expression));
        var v = p.ParseExpr();
        p.SkipWs();
        if (!p.End) throw new FormatException($"看不懂的位置：{p.Rest}");
        if (double.IsNaN(v) || double.IsInfinity(v)) throw new FormatException("结果不是有限数（除零或超出范围）");
        return v;
    }

    private static string Normalize(string s) => s
        .Replace('（', '(').Replace('）', ')').Replace('×', '*').Replace('÷', '/').Replace('－', '-').Replace('＋', '+')
        .Replace("**", "^").Replace('，', ',');

    private sealed class Parser(string s)
    {
        private int _i;
        public bool End => _i >= s.Length;
        public string Rest => s[_i..];
        public void SkipWs() { while (_i < s.Length && char.IsWhiteSpace(s[_i])) _i++; }
        private bool Eat(char c) { SkipWs(); if (_i < s.Length && s[_i] == c) { _i++; return true; } return false; }

        public double ParseExpr()
        {
            var v = ParseTerm();
            while (true)
            {
                if (Eat('+')) v += ParseTerm();
                else if (Eat('-')) v -= ParseTerm();
                else return v;
            }
        }

        private double ParseTerm()
        {
            var v = ParsePower();
            while (true)
            {
                if (Eat('*')) v *= ParsePower();
                else if (Eat('/')) v /= ParsePower();
                else return v;
            }
        }

        private double ParsePower()
        {
            var b = ParseUnary();
            return Eat('^') ? Math.Pow(b, ParsePower()) : b; // 右结合
        }

        private double ParseUnary()
        {
            if (Eat('-')) return -ParseUnary();
            if (Eat('+')) return ParseUnary();
            return ParseAtom();
        }

        private double ParseAtom()
        {
            SkipWs();
            if (Eat('('))
            {
                var v = ParseExpr();
                if (!Eat(')')) throw new FormatException("括号没配对");
                return v;
            }
            if (_i < s.Length && (char.IsDigit(s[_i]) || s[_i] == '.'))
            {
                var m = Regex.Match(s[_i..], @"^\d*\.?\d+([eE][-+]?\d+)?");
                _i += m.Length;
                return double.Parse(m.Value, CultureInfo.InvariantCulture);
            }
            var id = Regex.Match(s[_i..], @"^[A-Za-z_][A-Za-z0-9_]*");
            if (id.Success)
            {
                _i += id.Length;
                var name = id.Value.ToLowerInvariant();
                if (name == "pi") return Math.PI;
                if (name == "e") return Math.E;
                if (!Eat('(')) throw new FormatException($"不认识的名字：{id.Value}");
                var args = new List<double> { ParseExpr() };
                while (Eat(',')) args.Add(ParseExpr());
                if (!Eat(')')) throw new FormatException("函数括号没配对");
                return name switch
                {
                    "sqrt" => Math.Sqrt(args[0]),
                    "abs" => Math.Abs(args[0]),
                    "ln" => Math.Log(args[0]),
                    "log10" or "log" => Math.Log10(args[0]),
                    "exp" => Math.Exp(args[0]),
                    "round" => Math.Round(args[0], args.Count > 1 ? (int)args[1] : 0, MidpointRounding.AwayFromZero),
                    "min" => args.Min(),
                    "max" => args.Max(),
                    _ => throw new FormatException($"不支持的函数：{id.Value}")
                };
            }
            throw new FormatException($"看不懂的位置：{Rest}");
        }
    }

    // ── 单位换算 ─────────────────────────────────────────
    private enum Dim { Pressure, Temperature, Volume, Mass, Length, Time, Power, Energy, Area, Flow, MassFlow, Speed, Viscosity }

    /// <summary>单位 → (量纲, 换成该量纲基准单位的倍数)。温度另算（有零点偏移）。</summary>
    private static readonly Dictionary<string, (Dim Dim, double Factor)> Units = Build();

    private static Dictionary<string, (Dim, double)> Build()
    {
        var d = new Dictionary<string, (Dim, double)>(StringComparer.OrdinalIgnoreCase);
        void Add(Dim dim, double f, params string[] names) { foreach (var n in names) d[n] = (dim, f); }
        // 压力（基准 Pa）。表压/绝压不在这里区分：换算只管数值，口径由调用方说明
        Add(Dim.Pressure, 1, "Pa", "帕");
        Add(Dim.Pressure, 1e3, "kPa", "千帕");
        Add(Dim.Pressure, 1e6, "MPa", "兆帕");
        Add(Dim.Pressure, 1e5, "bar", "巴");
        Add(Dim.Pressure, 100, "mbar", "毫巴", "hPa");
        Add(Dim.Pressure, 101325, "atm", "标准大气压");
        Add(Dim.Pressure, 6894.757293168, "psi");
        Add(Dim.Pressure, 133.322387415, "mmHg", "Torr", "托");
        Add(Dim.Pressure, 98066.5, "kgf/cm2", "kgf/cm²", "at");
        Add(Dim.Pressure, 9.80665, "mmH2O", "mmWC");
        // 体积（基准 L）
        Add(Dim.Volume, 1, "L", "l", "升");
        Add(Dim.Volume, 1e-3, "mL", "ml", "毫升");
        Add(Dim.Volume, 1e3, "m3", "m³", "立方米", "方");
        Add(Dim.Volume, 1e-3, "cm3", "cm³", "cc");
        Add(Dim.Volume, 3.785411784, "gal", "USgal");
        // 质量（基准 kg）
        Add(Dim.Mass, 1, "kg", "公斤", "千克");
        Add(Dim.Mass, 1e-3, "g", "克");
        Add(Dim.Mass, 1e3, "t", "吨");
        Add(Dim.Mass, 0.45359237, "lb");
        // 长度（基准 m）
        Add(Dim.Length, 1, "m", "米");
        Add(Dim.Length, 1e-3, "mm", "毫米");
        Add(Dim.Length, 1e-2, "cm", "厘米");
        Add(Dim.Length, 0.0254, "in", "inch", "英寸");
        Add(Dim.Length, 0.3048, "ft");
        // 时间（基准 s）
        Add(Dim.Time, 1, "s", "sec", "秒");
        Add(Dim.Time, 60, "min", "分钟");
        Add(Dim.Time, 3600, "h", "hr", "小时");
        Add(Dim.Time, 86400, "d", "day", "天");
        // 功率（基准 W）、能量（基准 J）、面积（基准 m²）
        Add(Dim.Power, 1, "W", "瓦");
        Add(Dim.Power, 1e3, "kW", "千瓦");
        Add(Dim.Power, 745.69987158, "hp");
        Add(Dim.Energy, 1, "J");
        Add(Dim.Energy, 1e3, "kJ");
        Add(Dim.Energy, 3.6e6, "kWh", "度");
        Add(Dim.Energy, 4.184e3, "kcal");
        Add(Dim.Area, 1, "m2", "m²", "平方米");
        Add(Dim.Area, 1e-4, "cm2", "cm²");
        Add(Dim.Area, 1e-6, "mm2", "mm²");
        // 黏度（基准 Pa·s）
        Add(Dim.Viscosity, 1, "Pa·s", "Pa.s", "Pas");
        Add(Dim.Viscosity, 1e-3, "mPa·s", "mPa.s", "cP", "cp");
        return d;
    }

    private static readonly string[] TempNames = ["C", "°C", "℃", "摄氏度", "K", "开", "F", "°F", "℉"];

    /// <summary>换算：value 从 from 换到 to。支持复合流量单位（L/h、m3/h、mL/min、kg/h、m/s）。</summary>
    public static double Convert(double value, string from, string to)
    {
        from = from.Trim(); to = to.Trim();
        if (IsTemp(from) || IsTemp(to))
        {
            if (!IsTemp(from) || !IsTemp(to)) throw new FormatException($"「{from}」和「{to}」不是同一类量");
            return FromKelvin(ToKelvin(value, from), to);
        }
        var (df, ff) = Resolve(from);
        var (dt, ft) = Resolve(to);
        if (df != dt) throw new FormatException($"「{from}」和「{to}」不是同一类量");
        return value * ff / ft;
    }

    private static (Dim, double) Resolve(string unit)
    {
        var u = unit.Replace(" ", "");
        if (Units.TryGetValue(u, out var hit)) return hit;
        // 复合：A/B，A 是体积/质量/长度，B 是时间
        var slash = u.IndexOf('/');
        if (slash > 0 && Units.TryGetValue(u[..slash], out var top) && Units.TryGetValue(u[(slash + 1)..], out var bottom) && bottom.Item1 == Dim.Time)
        {
            var dim = top.Item1 switch
            {
                Dim.Volume => Dim.Flow,
                Dim.Mass => Dim.MassFlow,
                Dim.Length => Dim.Speed,
                _ => throw new FormatException($"不认识的单位：{unit}")
            };
            return (dim, top.Item2 / bottom.Item2);
        }
        throw new FormatException($"不认识的单位：{unit}");
    }

    private static bool IsTemp(string u) => TempNames.Contains(u.Replace(" ", ""), StringComparer.OrdinalIgnoreCase);

    private static double ToKelvin(double v, string u) => u.Replace(" ", "").ToUpperInvariant() switch
    {
        "K" or "开" => v,
        "F" or "°F" or "℉" => (v - 32) * 5 / 9 + 273.15,
        _ => v + 273.15
    };

    private static double FromKelvin(double k, string u) => u.Replace(" ", "").ToUpperInvariant() switch
    {
        "K" or "开" => k,
        "F" or "°F" or "℉" => (k - 273.15) * 9 / 5 + 32,
        _ => k - 273.15
    };

    /// <summary>给人看的数字：去掉浮点尾巴，太大太小用科学计数法。</summary>
    public static string Format(double v)
    {
        if (v == 0) return "0";
        var a = Math.Abs(v);
        if (a >= 1e9 || a < 1e-6) return v.ToString("0.######E+0", CultureInfo.InvariantCulture);
        return Math.Round(v, 8).ToString("0.########", CultureInfo.InvariantCulture);
    }
}
