// Prints the public surface of BmSDK types: apidump <sdkdir> <TypeName> [TypeName...]
// A type name can end with * to list matching type names only.
using System.Reflection;

var sdk = args[0];
var paths = Directory.GetFiles(sdk, "*.dll").Where(p => !p.Contains("Native") && !p.Contains("coreclr") && !p.Contains("steam_api")).ToList();
if (!paths.Any(p => p.EndsWith("System.Private.CoreLib.dll"))) paths.AddRange(Directory.GetFiles(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));
using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths), "System.Private.CoreLib");
var asmName = Environment.GetEnvironmentVariable("APIDUMP_ASM") ?? "BmSDK.dll";
var asm = mlc.LoadFromAssemblyPath(Path.Combine(sdk, asmName));
var types = asm.GetTypes();
const BindingFlags F = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
string N(Type t) => t.IsGenericType ? t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(N)) + ">" : t.Name;
foreach (var q in args.Skip(1))
{
    if (q.EndsWith('*'))
    {
        foreach (var t in types.Where(t => t.Name.StartsWith(q.TrimEnd('*'))).OrderBy(t => t.Name)) Console.WriteLine($"{t.FullName} : {t.BaseType?.Name}");
        continue;
    }
    foreach (var t in types.Where(t => t.Name == q || t.FullName == q))
    {
        Console.WriteLine($"== {t.FullName} : {t.BaseType?.FullName}");
        foreach (var f in t.GetFields(F)) Console.WriteLine($"  field {N(f.FieldType)} {f.Name}");
        foreach (var p in t.GetProperties(F)) Console.WriteLine($"  prop  {N(p.PropertyType)} {p.Name}");
        foreach (var m in t.GetMethods(F).Where(m => !m.IsSpecialName))
            Console.WriteLine($"  {(m.IsStatic ? "static " : "")}{N(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(p => N(p.ParameterType) + " " + p.Name))})");
        foreach (var c in t.GetConstructors()) Console.WriteLine($"  ctor({string.Join(", ", c.GetParameters().Select(p => N(p.ParameterType) + " " + p.Name))})");
        foreach (var n in t.GetNestedTypes()) Console.WriteLine($"  nested {n.Name} {(n.IsEnum ? string.Join(",", n.GetFields(BindingFlags.Public|BindingFlags.Static).Select(f=>f.Name)) : "")}");
    }
}
