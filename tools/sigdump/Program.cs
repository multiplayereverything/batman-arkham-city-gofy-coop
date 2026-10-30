// Prints full C# signatures of BmSDK methods: sigdump <sdkdir> <Type> <Method> [Method...]
using System.Reflection;
var sdk = args[0];
var paths = Directory.GetFiles(sdk, "*.dll").Where(p => !p.Contains("Native") && !p.Contains("coreclr") && !p.Contains("steam_api")).ToList();
if (!paths.Any(p => p.EndsWith("System.Private.CoreLib.dll"))) paths.AddRange(Directory.GetFiles(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));
using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths), "System.Private.CoreLib");
var asm = mlc.LoadFromAssemblyPath(Path.Combine(sdk, "BmSDK.dll"));
string N(Type t) { var b = t.IsByRef ? "ref " + N(t.GetElementType()!) : t.IsGenericType ? (t.Namespace + "." + t.Name.Split('`')[0]) + "<" + string.Join(",", t.GetGenericArguments().Select(N)) + ">" : (t.FullName ?? t.Name).Replace('+', '.'); return b; }
if (args[1] == "--fields")
{
    foreach (var tn in args.Skip(2))
        foreach (var t in asm.GetTypes().Where(t => t.FullName!.Replace('+', '.').EndsWith(tn)))
        {
            Console.WriteLine("== " + t.FullName);
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance)) Console.WriteLine($"  field {N(f.FieldType)} {f.Name}");
            foreach (var pr in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)) Console.WriteLine($"  prop  {N(pr.PropertyType)} {pr.Name}");
        }
    return;
}
var type = asm.GetTypes().First(t => t.Name == args[1]);
foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(m => args.Skip(2).Contains(m.Name)))
    Console.WriteLine($"{N(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(p => N(p.ParameterType) + " " + p.Name))})");
