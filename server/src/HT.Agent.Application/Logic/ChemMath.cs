using System.Globalization;
using System.Text;

namespace HT.Agent.Application.Logic;

/// <summary>化工组件的系统核对：化学式拆元素、SMILES 算分子式和分子量、反应式配平、物料衡算。
/// 模型只给结构化内容，这些数由系统算——算出来跟模型说的不一致，就告诉模型改。</summary>
public static class ChemMath
{
    /// <summary>标准原子量（IUPAC 常用值，够工程计算用）。</summary>
    public static readonly IReadOnlyDictionary<string, double> Weights = new Dictionary<string, double>
    {
        ["H"] = 1.008, ["He"] = 4.0026, ["Li"] = 6.94, ["Be"] = 9.0122, ["B"] = 10.81, ["C"] = 12.011, ["N"] = 14.007, ["O"] = 15.999,
        ["F"] = 18.998, ["Ne"] = 20.180, ["Na"] = 22.990, ["Mg"] = 24.305, ["Al"] = 26.982, ["Si"] = 28.085, ["P"] = 30.974, ["S"] = 32.06,
        ["Cl"] = 35.45, ["Ar"] = 39.948, ["K"] = 39.098, ["Ca"] = 40.078, ["Sc"] = 44.956, ["Ti"] = 47.867, ["V"] = 50.942, ["Cr"] = 51.996,
        ["Mn"] = 54.938, ["Fe"] = 55.845, ["Co"] = 58.933, ["Ni"] = 58.693, ["Cu"] = 63.546, ["Zn"] = 65.38, ["Ga"] = 69.723, ["Ge"] = 72.630,
        ["As"] = 74.922, ["Se"] = 78.971, ["Br"] = 79.904, ["Kr"] = 83.798, ["Rb"] = 85.468, ["Sr"] = 87.62, ["Y"] = 88.906, ["Zr"] = 91.224,
        ["Nb"] = 92.906, ["Mo"] = 95.95, ["Ru"] = 101.07, ["Rh"] = 102.91, ["Pd"] = 106.42, ["Ag"] = 107.87, ["Cd"] = 112.41, ["In"] = 114.82,
        ["Sn"] = 118.71, ["Sb"] = 121.76, ["Te"] = 127.60, ["I"] = 126.90, ["Xe"] = 131.29, ["Cs"] = 132.91, ["Ba"] = 137.33, ["La"] = 138.91,
        ["Ce"] = 140.12, ["Nd"] = 144.24, ["Hf"] = 178.49, ["Ta"] = 180.95, ["W"] = 183.84, ["Re"] = 186.21, ["Os"] = 190.23, ["Ir"] = 192.22,
        ["Pt"] = 195.08, ["Au"] = 196.97, ["Hg"] = 200.59, ["Tl"] = 204.38, ["Pb"] = 207.2, ["Bi"] = 208.98, ["Th"] = 232.04, ["U"] = 238.03,
    };

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(ch switch
            {
                >= '₀' and <= '₉' => (char)('0' + (ch - '₀')),
                '·' or '•' or '∙' or '・' or '*' => '.',
                '（' => '(', '）' => ')', '［' => '[', '］' => ']',
                _ => ch,
            });
        return sb.ToString().Trim();
    }

    /// <summary>化学式 → 元素个数。认括号、方括号、结晶水（CuSO4·5H2O）、末尾电荷（SO4^2-、Fe3+ 忽略电荷）。认不出返回 null。</summary>
    public static Dictionary<string, int>? ParseFormula(string formula)
    {
        var s = Normalize(formula);
        if (s.Length == 0) return null;
        // 末尾电荷：^2-、2+、+、-
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\^?\d*[+\-−]$", "");
        var total = new Dictionary<string, int>();
        foreach (var part in s.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim();
            var i = 0; var mult = 0;
            while (i < p.Length && char.IsDigit(p[i])) { mult = mult * 10 + (p[i] - '0'); i++; }
            if (mult == 0) mult = 1;
            var counts = ParseGroup(p, ref i);
            if (counts is null || i != p.Length || counts.Count == 0) return null;
            foreach (var (k, v) in counts) total[k] = total.GetValueOrDefault(k) + v * mult;
        }
        return total.Count == 0 ? null : total;
    }

    private static Dictionary<string, int>? ParseGroup(string s, ref int i)
    {
        var map = new Dictionary<string, int>();
        while (i < s.Length)
        {
            var c = s[i];
            if (c is ')' or ']') break;
            Dictionary<string, int> inner;
            if (c is '(' or '[')
            {
                var close = c == '(' ? ')' : ']';
                i++;
                var g = ParseGroup(s, ref i);
                if (g is null || i >= s.Length || s[i] != close) return null;
                i++;
                inner = g;
            }
            else if (char.IsUpper(c))
            {
                var sym = c.ToString(); i++;
                if (i < s.Length && char.IsLower(s[i])) { sym += s[i]; i++; }
                if (!Weights.ContainsKey(sym)) return null;
                inner = new Dictionary<string, int> { [sym] = 1 };
            }
            else return null;
            var n = 0; var has = false;
            while (i < s.Length && char.IsDigit(s[i])) { n = n * 10 + (s[i] - '0'); i++; has = true; }
            if (!has) n = 1;
            foreach (var (k, v) in inner) map[k] = map.GetValueOrDefault(k) + v * n;
        }
        return map;
    }

    /// <summary>Hill 顺序写分子式：有碳先 C 再 H，其余按字母。</summary>
    public static string Hill(IReadOnlyDictionary<string, int> counts)
    {
        var keys = counts.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
        IEnumerable<string> order = counts.ContainsKey("C") && counts["C"] > 0
            ? new[] { "C", "H" }.Where(keys.Contains).Concat(keys.Where(k => k is not ("C" or "H")).OrderBy(k => k, StringComparer.Ordinal))
            : keys.OrderBy(k => k, StringComparer.Ordinal);
        return string.Concat(order.Select(k => counts[k] == 1 ? k : k + counts[k].ToString(CultureInfo.InvariantCulture)));
    }

    public static double Mass(IReadOnlyDictionary<string, int> counts) => counts.Sum(kv => Weights[kv.Key] * kv.Value);

    public record SmilesResult(string Formula, double Weight, int Charge, Dictionary<string, int> Counts);

    private static readonly Dictionary<string, int[]> Valences = new()
    {
        ["B"] = [3], ["C"] = [4], ["N"] = [3, 5], ["O"] = [2], ["P"] = [3, 5], ["S"] = [2, 4, 6],
        ["F"] = [1], ["Cl"] = [1], ["Br"] = [1], ["I"] = [1],
    };

    private sealed class Atom
    {
        public string Sym = "";
        public bool Aromatic;
        public bool Bracket;
        public int Hs;
        public int Charge;
        public double BondSum;
    }

    /// <summary>SMILES → 分子式、分子量。支持常用写法（有机子集、方括号原子、支链、环闭合、芳香小写、点号分开的盐）。
    /// 写错了（括号不配、环没闭合、不认识的元素）抛 FormatException，说明错在哪。</summary>
    public static SmilesResult FromSmiles(string smiles)
    {
        var s = smiles.Trim();
        if (s.Length == 0 || s.Length > 500) throw new FormatException("SMILES 为空或太长");
        var atoms = new List<Atom>();
        var stack = new Stack<int>();
        var rings = new Dictionary<int, (int Atom, double Order)>();
        int prev = -1;
        double? pending = null;
        var i = 0;
        void Bond(int a, int b, double order)
        {
            atoms[a].BondSum += order; atoms[b].BondSum += order;
        }
        double DefaultOrder(int a, int b) => atoms[a].Aromatic && atoms[b].Aromatic ? 1 : 1;
        while (i < s.Length)
        {
            var c = s[i];
            switch (c)
            {
                case '(':
                    if (prev < 0) throw new FormatException("支链括号前面没有原子");
                    stack.Push(prev); i++; continue;
                case ')':
                    if (stack.Count == 0) throw new FormatException("右括号多了");
                    prev = stack.Pop(); i++; continue;
                case '-': pending = 1; i++; continue;
                case '=': pending = 2; i++; continue;
                case '#': pending = 3; i++; continue;
                case '$': pending = 4; i++; continue;
                case ':': pending = 1; i++; continue;
                case '/': case '\\': i++; continue;
                case '.': prev = -1; pending = null; i++; continue;
            }
            if (char.IsDigit(c) || c == '%')
            {
                int num;
                if (c == '%')
                {
                    if (i + 2 >= s.Length || !char.IsDigit(s[i + 1]) || !char.IsDigit(s[i + 2])) throw new FormatException("% 后面要跟两位环编号");
                    num = (s[i + 1] - '0') * 10 + (s[i + 2] - '0'); i += 3;
                }
                else { num = c - '0'; i++; }
                if (prev < 0) throw new FormatException("环编号前面没有原子");
                if (rings.Remove(num, out var open))
                {
                    var order = pending ?? (open.Order > 0 ? open.Order : DefaultOrder(open.Atom, prev));
                    Bond(open.Atom, prev, order);
                }
                else rings[num] = (prev, pending ?? 0);
                pending = null;
                continue;
            }
            Atom atom;
            if (c == '[')
            {
                var end = s.IndexOf(']', i);
                if (end < 0) throw new FormatException("方括号没闭合");
                atom = ParseBracket(s[(i + 1)..end]);
                i = end + 1;
            }
            else
            {
                string sym;
                if (c == 'C' && i + 1 < s.Length && s[i + 1] == 'l') { sym = "Cl"; i += 2; }
                else if (c == 'B' && i + 1 < s.Length && s[i + 1] == 'r') { sym = "Br"; i += 2; }
                else if ("BCNOPSFI".Contains(c)) { sym = c.ToString(); i++; }
                else if ("bcnops".Contains(c)) { atom = new Atom { Sym = char.ToUpperInvariant(c).ToString(), Aromatic = true }; i++; goto add; }
                else throw new FormatException($"第 {i + 1} 个字符「{c}」认不出来（不在方括号里的原子只能是 B C N O P S F Cl Br I）");
                atom = new Atom { Sym = sym };
            }
            add:
            atoms.Add(atom);
            var idx = atoms.Count - 1;
            if (prev >= 0) Bond(prev, idx, pending ?? DefaultOrder(prev, idx));
            pending = null;
            prev = idx;
        }
        if (stack.Count > 0) throw new FormatException("左括号没有配对的右括号");
        if (rings.Count > 0) throw new FormatException($"环编号 {string.Join("、", rings.Keys)} 没有闭合");
        if (atoms.Count == 0) throw new FormatException("没有原子");

        var counts = new Dictionary<string, int>();
        var charge = 0;
        foreach (var a in atoms)
        {
            counts[a.Sym] = counts.GetValueOrDefault(a.Sym) + 1;
            charge += a.Charge;
            var h = a.Hs;
            if (!a.Bracket && Valences.TryGetValue(a.Sym, out var vals))
            {
                // 芳香的 C、N、P、B 还要分一个电子给共轭体系；芳香 O、S 用孤对电子，不算
                var used = (int)Math.Round(a.BondSum) + (a.Aromatic && a.Sym is "C" or "N" or "P" or "B" ? 1 : 0);
                var v = vals.FirstOrDefault(x => x >= used, -1);
                h = v < 0 ? 0 : v - used;
            }
            if (h > 0) counts["H"] = counts.GetValueOrDefault("H") + h;
        }
        return new SmilesResult(Hill(counts), Math.Round(Mass(counts), 3), charge, counts);
    }

    private static Atom ParseBracket(string body)
    {
        var i = 0;
        while (i < body.Length && char.IsDigit(body[i])) i++; // 同位素
        if (i >= body.Length) throw new FormatException($"[{body}] 里没有元素");
        var a = new Atom { Bracket = true };
        var c = body[i];
        if (char.IsLower(c))
        {
            // 芳香小写：c n o s p b，或 se、as
            var two = i + 1 < body.Length && char.IsLower(body[i + 1]) ? body.Substring(i, 2) : null;
            if (two is "se" or "as") { a.Sym = char.ToUpperInvariant(two[0]) + two[1..]; i += 2; }
            else { a.Sym = char.ToUpperInvariant(c).ToString(); i++; }
            a.Aromatic = true;
        }
        else
        {
            var sym = c.ToString(); i++;
            if (i < body.Length && char.IsLower(body[i]) && Weights.ContainsKey(sym + body[i])) { sym += body[i]; i++; }
            a.Sym = sym;
        }
        if (!Weights.ContainsKey(a.Sym)) throw new FormatException($"不认识的元素「{a.Sym}」");
        while (i < body.Length && body[i] == '@') i++;
        if (i < body.Length && body[i] == 'H')
        {
            i++; var n = 0; var has = false;
            while (i < body.Length && char.IsDigit(body[i])) { n = n * 10 + (body[i] - '0'); i++; has = true; }
            a.Hs = has ? n : 1;
        }
        if (i < body.Length && body[i] is '+' or '-')
        {
            var sign = body[i] == '+' ? 1 : -1; var n = 0; var reps = 0;
            while (i < body.Length && body[i] is '+' or '-') { reps++; i++; }
            var has = false;
            while (i < body.Length && char.IsDigit(body[i])) { n = n * 10 + (body[i] - '0'); i++; has = true; }
            a.Charge = sign * (has ? n : reps);
        }
        return a;
    }

    public record Species(double Coef, string Formula);

    /// <summary>反应式配平核对：返回每种元素左右差多少（左 − 右），配平了就是空的。化学式认不出返回 null 并给原因。</summary>
    public static (Dictionary<string, double>? Diff, string? Error) Balance(IEnumerable<Species> left, IEnumerable<Species> right)
    {
        var sum = new Dictionary<string, double>();
        foreach (var (side, sign) in new[] { (left, 1.0), (right, -1.0) })
            foreach (var sp in side)
            {
                var counts = ParseFormula(sp.Formula);
                if (counts is null) return (null, $"化学式「{sp.Formula}」认不出来");
                var coef = sp.Coef <= 0 ? 1 : sp.Coef;
                foreach (var (k, v) in counts) sum[k] = sum.GetValueOrDefault(k) + sign * coef * v;
            }
        var diff = sum.Where(kv => Math.Abs(kv.Value) > 1e-6).ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 4));
        return (diff, null);
    }

    public record Stream(string Id, string? From, string? To, double? Flow);
    public record UnitBalance(string Unit, double In, double Out, bool Ok);

    /// <summary>物料衡算：每台设备进 = 出，整个系统进料 = 产品。差超过 tolerance（默认 1%）算不平。
    /// 物流没写流量的设备不判断。</summary>
    public static (List<UnitBalance> Units, UnitBalance? Overall) MassBalance(IReadOnlyList<Stream> streams, double tolerance = 0.01)
    {
        static bool Edge(string? x) => string.IsNullOrWhiteSpace(x);
        var units = streams.SelectMany(x => new[] { x.From, x.To }).Where(x => !Edge(x)).Select(x => x!).Distinct().ToList();
        var list = new List<UnitBalance>();
        bool Ok(double a, double b) => Math.Abs(a - b) <= Math.Max(1e-9, Math.Max(Math.Abs(a), Math.Abs(b)) * tolerance);
        foreach (var u in units)
        {
            var ins = streams.Where(x => x.To == u).ToList();
            var outs = streams.Where(x => x.From == u).ToList();
            if (ins.Count == 0 || outs.Count == 0 || ins.Concat(outs).Any(x => x.Flow is null)) continue;
            double a = ins.Sum(x => x.Flow!.Value), b = outs.Sum(x => x.Flow!.Value);
            list.Add(new UnitBalance(u, a, b, Ok(a, b)));
        }
        var feeds = streams.Where(x => Edge(x.From) && !Edge(x.To)).ToList();
        var prods = streams.Where(x => Edge(x.To) && !Edge(x.From)).ToList();
        UnitBalance? overall = null;
        if (feeds.Count > 0 && prods.Count > 0 && feeds.Concat(prods).All(x => x.Flow is not null))
        {
            double a = feeds.Sum(x => x.Flow!.Value), b = prods.Sum(x => x.Flow!.Value);
            overall = new UnitBalance("全系统", a, b, Ok(a, b));
        }
        return (list, overall);
    }
}
