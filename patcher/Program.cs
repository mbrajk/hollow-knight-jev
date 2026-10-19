using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;
using System.Text.Json;

// Windows vanilla 1.5.12620. New versions need explicit compatibility review.
const string SupportedInput = "E9048EF6A633970F735E01EC166D3959F610EAEA7A88D827D48D67B1E5FB87BD";
if (args.Length != 4) throw new ArgumentException("Usage: Patcher original-game.dll helper.dll candidate.dll managed-directory");
string inputPath = Path.GetFullPath(args[0]), helperPath = Path.GetFullPath(args[1]), outputPath = Path.GetFullPath(args[2]);
if (StringComparer.OrdinalIgnoreCase.Equals(inputPath, outputPath) || StringComparer.OrdinalIgnoreCase.Equals(helperPath, outputPath))
    throw new ArgumentException("Output must be separate from both input files.");
if (Hash(inputPath) != SupportedInput) throw new InvalidOperationException("Unsupported game assembly. Only the verified unmodified Windows 1.5.12620 build is supported.");
if (File.Exists(outputPath)) throw new IOException("Candidate already exists; use a fresh output file.");
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetFullPath(args[3]));
resolver.AddSearchDirectory(Path.GetDirectoryName(helperPath)!);
using var game = AssemblyDefinition.ReadAssembly(inputPath, new ReaderParameters { AssemblyResolver = resolver });
using var helper = AssemblyDefinition.ReadAssembly(helperPath, new ReaderParameters { AssemblyResolver = resolver });
if (game.MainModule.AssemblyReferences.Any(r => r.Name is "HollowKnightTrial" or "BossHealthDisplay")) throw new Exception("Expected vanilla game, not a modified assembly.");
if (helper.Name.Name != "HollowKnightTrial") throw new Exception("Unexpected helper identity.");
var bridge = helper.MainModule.Types.Single(t => t.FullName == "HollowKnightTrial.Bridge");
var startup = game.MainModule.Types.Single(t => t.Name == "GameManager").Methods.Single(m => m.Name == "Start" && !m.HasParameters);
var input = game.MainModule.Types.Single(t => t.FullName == "InControl.PlayerAction").Methods.Single(m => m.FullName == "System.Void InControl.PlayerAction::Update(System.UInt64,System.Single,InControl.InputDevice)");
var before = game.MainModule.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).ToDictionary(Key, m => Signature(m));
var il = startup.Body.GetILProcessor();
il.InsertBefore(startup.Body.Instructions[0], il.Create(OpCodes.Call, game.MainModule.ImportReference(bridge.Methods.Single(m => m.Name == "Install" && m.IsStatic))));
il = input.Body.GetILProcessor();
var first = input.Body.Instructions[0];
foreach (var instruction in new[] { il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldarg_1), il.Create(OpCodes.Ldarg_2), il.Create(OpCodes.Call, game.MainModule.ImportReference(bridge.Methods.Single(m => m.Name == "InputOverride" && m.IsStatic))), il.Create(OpCodes.Brfalse, first), il.Create(OpCodes.Ret) }) il.InsertBefore(first, instruction);
string temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
try
{
    game.Write(temporary);
    using (var patched = AssemblyDefinition.ReadAssembly(temporary))
    {
        var after = patched.MainModule.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).ToArray();
        if (after.Length != before.Count || after.Any(m => !before.ContainsKey(Key(m)))) throw new Exception("Method inventory changed.");
        var changed = after.Where(m => before[Key(m)] != Signature(m)).ToArray();
        if (changed.Length != 2 || !changed.Any(m => m.FullName == startup.FullName) || !changed.Any(m => m.FullName == input.FullName)) throw new Exception("Unexpected changed methods.");
        foreach (var m in changed)
            if (Signature(m, m.FullName == startup.FullName ? 1 : 6) != before[Key(m)]) throw new Exception("Original method body, variables, or exception handlers changed.");
    }
    File.Move(temporary, outputPath);
    Console.WriteLine(JsonSerializer.Serialize(new { schema = 1, supportedVersion = "1.5.12620-windows", inputSHA256 = Hash(inputPath), patchedSHA256 = Hash(outputPath), helperSHA256 = Hash(helperPath), changedMethods = new[] { startup.FullName, input.FullName }, originalBodiesPreserved = true }, new JsonSerializerOptions { WriteIndented = true }));
}
finally { if (File.Exists(temporary)) File.Delete(temporary); }
static string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
static string Key(MethodDefinition m) => m.FullName + "#" + m.DeclaringType.Methods.IndexOf(m);
static string Signature(MethodDefinition m, int skip = 0)
{
    string Target(Instruction? i) => i == null ? "null" : "@" + (m.Body.Instructions.IndexOf(i) - skip);
    string body = string.Join("\n", m.Body.Instructions.Skip(skip).Select(i => i.OpCode + " " + (i.Operand switch { Instruction t => Target(t), Instruction[] ts => string.Join(",", ts.Select(Target)), _ => i.Operand?.ToString() ?? "" })));
    string locals = string.Join(",", m.Body.Variables.Select(v => v.VariableType.FullName));
    string handlers = string.Join("\n", m.Body.ExceptionHandlers.Select(h => $"{h.HandlerType}:{h.CatchType?.FullName}:{Target(h.TryStart)}:{Target(h.TryEnd)}:{Target(h.HandlerStart)}:{Target(h.HandlerEnd)}:{Target(h.FilterStart)}"));
    return $"{m.Attributes}|{m.ImplAttributes}|{m.Body.InitLocals}|{locals}\n{handlers}\n{body}";
}
