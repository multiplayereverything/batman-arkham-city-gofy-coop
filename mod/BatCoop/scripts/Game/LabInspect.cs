#nullable enable
// Lab only: reads, writes and calls game objects by reflection, so the lab can study a live game without a
// rebuild. Expressions start at a root (pawn, pc, wi, gri, puppet, npc, last, obj:<path>) and walk
// Name, Name[i] and Name() segments separated by dots. Reached only through the lab bridge (LabPort).
using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using BmSDK;
using BmSDK.BmGame;

namespace BatCoop.Game;

static class LabInspect
{
    static object? _last;
    static readonly Assembly Sdk = typeof(GameObject).Assembly;

    public static object? Eval(string expr, Func<string, object?> extraRoot)
    {
        // An object path contains dots itself: "obj:<path>" ends at '|' ("obj:Pkg.Group.Name|Prop.Sub").
        if (expr.StartsWith("obj:") && !expr.Contains('|')) return _last = Root(expr, extraRoot);
        if (expr.StartsWith("obj:") && expr.Contains('|'))
        {
            int bar = expr.IndexOf('|');
            object? o = Root(expr[..bar], extraRoot);
            foreach (var seg in Split(expr[(bar + 1)..])) o = Step(o, seg);
            _last = o;
            return o;
        }
        var segs = Split(expr);
        if (segs.Count == 0) throw new ArgumentException("empty expression");
        object? cur = Root(segs[0], extraRoot);
        for (int i = 1; i < segs.Count; i++) cur = Step(cur, segs[i]);
        _last = cur;
        return cur;
    }

    static object? Root(string seg, Func<string, object?> extraRoot)
    {
        if (seg.StartsWith("obj:")) return BmSDK.Framework.Game.FindObject<GameObject>(seg[4..]);
        if (seg.StartsWith("cls:")) return FindType(seg[4..]);
        return seg switch
        {
            "pawn" => BmSDK.Framework.Game.GetPlayerPawn(0),
            "pc" => BmSDK.Framework.Game.GetPlayerController(0),
            "wi" => BmSDK.Framework.Game.GetWorldInfo(),
            "gri" => BmSDK.Framework.Game.GetGameRI(),
            "gi" => BmSDK.Framework.Game.GetGameInfo(),
            "pd" => BmSDK.Framework.Game.GetPersistentData(),
            "engine" => BmSDK.Framework.Game.GetEngine(),
            "last" => _last,
            _ => extraRoot(seg),
        };
    }

    /// <summary>Splits on dots outside brackets and parentheses.</summary>
    static List<string> Split(string expr)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;
        foreach (var c in expr)
        {
            if (c is '[' or '(') depth++;
            if (c is ']' or ')') depth--;
            if (c == '.' && depth == 0) { list.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    static object? Step(object? cur, string seg)
    {
        if (cur == null) throw new NullReferenceException($"null before '{seg}'");
        int idx = -1;
        var b = seg.IndexOf('[');
        if (b > 0 && seg.EndsWith(']')) { idx = int.Parse(seg[(b + 1)..^1]); seg = seg[..b]; }
        object? v;
        if (seg.EndsWith("()")) v = Invoke(cur, seg[..^2], []);
        else v = Get(cur, seg);
        if (idx >= 0) v = Index(v, idx);
        return v;
    }

    public static Type? FindType(string name) =>
        Sdk.GetTypes().FirstOrDefault(t => t.Name == name || t.FullName == name);

    static object? Get(object cur, string name)
    {
        var t = cur as Type ?? cur.GetType();
        var target = cur is Type ? null : cur;
        const BindingFlags F = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        var p = t.GetProperty(name, F);
        if (p != null) return p.GetValue(target);
        var f = t.GetField(name, F);
        if (f != null) return f.GetValue(target);
        throw new MissingMemberException(t.Name, name);
    }

    static object? Index(object? v, int i)
    {
        if (v is IList l) return l[i];
        if (v is IEnumerable e) return e.Cast<object?>().ElementAt(i);
        throw new InvalidOperationException($"{v?.GetType().Name} is not a list");
    }

    public static void Set(object cur, string name, string value)
    {
        var t = cur.GetType();
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                ?? throw new MissingMemberException(t.Name, name);
        p.SetValue(cur, Convert(value, p.PropertyType));
    }

    public static object? Invoke(object cur, string name, string[] args)
    {
        var t = cur as Type ?? cur.GetType();
        var target = cur is Type ? null : cur;
        var ms = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                  .Where(m => m.Name == name && !m.IsGenericMethodDefinition).ToList();
        var m = ms.FirstOrDefault(m => m.GetParameters().Length == args.Length)
                ?? ms.FirstOrDefault(m => m.GetParameters().Count(p => !p.IsOptional && !p.HasDefaultValue) <= args.Length && m.GetParameters().Length >= args.Length)
                ?? throw new MissingMethodException(t.Name, name);
        var ps = m.GetParameters();
        var vals = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var pt = ps[i].ParameterType.IsByRef ? ps[i].ParameterType.GetElementType()! : ps[i].ParameterType;
            vals[i] = i < args.Length ? Convert(args[i], pt) : ps[i].HasDefaultValue ? ps[i].DefaultValue : Default(pt);
        }
        return m.Invoke(target, vals);
    }

    static object? Default(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;

    static object? Convert(string s, Type t)
    {
        if (s == "null") return null;
        if (s == "$last") return _last;
        if (s.StartsWith("obj:")) return BmSDK.Framework.Game.FindObject<GameObject>(s[4..]);
        if (t == typeof(string)) return s;
        if (t == typeof(FName)) return new FName(s);
        if (t == typeof(FString)) return new FString(s);
        if (t == typeof(bool)) return s is "1" or "true" or "True";
        if (t.IsEnum) return int.TryParse(s, out var ei) ? System.Enum.ToObject(t, ei) : System.Enum.Parse(t, s);
        if (t == typeof(Vector3)) { var p = s.Split(','); return new Vector3(Fl(p[0]), Fl(p[1]), Fl(p[2])); }
        if (t == typeof(Rotator)) { var p = s.Split(','); return new Rotator(Fl(p[0]), Fl(p[1]), Fl(p[2])); }
        if (t == typeof(Class) || typeof(Class).IsAssignableFrom(t))
            return BmSDK.Framework.Game.FindObject<Class>(s);
        return System.Convert.ChangeType(s, t, CultureInfo.InvariantCulture);
    }

    static float Fl(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>A value as JSON: scalars as they are, game objects as their path plus (at depth) their properties.</summary>
    public static string Describe(object? v, int depth, string filter = "")
    {
        if (v == null) return "null";
        if (v is string or bool or float or double or int or uint or long or short or byte or Vector3) return LabBridge.Value(v);
        if (v is System.Enum || v is FName || v is FString) return LabBridge.Value(v.ToString());
        if (v is Type ty) return LabBridge.Value("type " + ty.FullName);
        if (v is GameObject go && depth <= 0) return LabBridge.Value(Short(go));
        if (v is IEnumerable e && v is not GameObject)
        {
            var items = e.Cast<object?>().Take(64).Select(x => Describe(x, depth - 1, filter)).ToList();
            return "[" + string.Join(",", items) + "]";
        }
        if (depth <= 0) return LabBridge.Value(v.ToString() ?? "");
        var sb = new StringBuilder("{");
        sb.Append("\"_type\":").Append(LabBridge.Value(v.GetType().Name));
        if (v is GameObject g) sb.Append(",\"_path\":").Append(LabBridge.Value(SafePath(g)));
        foreach (var p in v.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || p.Name is "DefaultObject") continue;
            if (filter.Length > 0 && !p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            string val;
            try { val = Describe(p.GetValue(v), depth - 1, ""); }
            catch (Exception ex) { val = LabBridge.Value("!" + (ex.InnerException ?? ex).GetType().Name); }
            sb.Append(",\"").Append(p.Name).Append("\":").Append(val);
        }
        return sb.Append('}').ToString();
    }

    static string Short(GameObject g) => g.IsValid ? $"{g.GetType().Name} {SafePath(g)}" : "invalid";

    static string SafePath(GameObject g)
    {
        try { return g.GetPathName(null); } catch { return "?"; }
    }

    /// <summary>Methods of a type whose name contains <paramref name="like"/>.</summary>
    public static List<string> Methods(object v, string like)
    {
        var t = v as Type ?? v.GetType();
        return t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(m => !m.IsSpecialName && m.Name.Contains(like, StringComparison.OrdinalIgnoreCase))
            .Select(m => $"{m.ReturnType.Name} {m.DeclaringType?.Name}.{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})")
            .Distinct().Take(150).ToList();
    }

    /// <summary>Live objects of a BmSDK class by name.</summary>
    public static List<object> Find(string typeName, int take)
    {
        var t = FindType(typeName) ?? throw new ArgumentException("no type " + typeName);
        var m = typeof(BmSDK.Framework.Game).GetMethods().First(x => x.Name == "FindObjects" && x.IsGenericMethodDefinition).MakeGenericMethod(t);
        return ((IEnumerable)m.Invoke(null, null)!).Cast<object>().Take(take).ToList();
    }
}
