using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
// usage: Patcher dump <dll> <TypeName> <method>   -- prints IL
//        Patcher patch <in.dll> <out.dll>          -- Shared-topology SERVER asymmetry patch:
//   the cloud Fusion plugin reads the PluginVersion varint clients write before each object's
//   word list, but does not write one towards clients (the SDK client reader never consumes it).
//   The SDK's StateReplicator is symmetric, so for a headless Simulation.Server: skip the write
//   when the simulation is a Server, and read it when the simulation is a Server.
var mode = args[0];
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1])));
foreach (var extra in Environment.GetEnvironmentVariable("PATCHER_SEARCH")?.Split(':') ?? Array.Empty<string>()) resolver.AddSearchDirectory(extra);
var asm = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { ReadWrite = false, AssemblyResolver = resolver });
TypeDefinition Find(string name) => asm.MainModule.GetTypes().First(t => t.FullName == name);
var repl = Find("Fusion.Simulation/StateReplicator");
if (mode == "dump") {
  var m = Find(args[2]).Methods.First(x => x.Name == args[3]);
  int i = 0; foreach (var ins in m.Body.Instructions) Console.WriteLine($"{i++,4} {ins}");
  return;
}
if (mode == "patch") {
  var write = repl.Methods.First(x => x.Name == "WriteObject_WriteData");
  var read = repl.Methods.First(x => x.Name == "ReadObjectUpdate_GetWords");
  write.Body.SimplifyMacros(); read.Body.SimplifyMacros();
  var wi = write.Body.Instructions;
  var simField = (FieldReference)wi.First(x => x.OpCode == OpCodes.Ldfld && ((FieldReference)x.Operand).Name == "_simulation").Operand;
  var serverType = (TypeReference)wi.First(x => x.OpCode == OpCodes.Isinst).Operand;
  var getTopology = (MethodReference)wi.First(x => x.OpCode == OpCodes.Callvirt && ((MethodReference)x.Operand).Name == "get_Topology").Operand;
  // --- writer: find `ldfld PluginVersion`, then the brfalse before it; insert `ldloc.0(isServer); brtrue target`.
  int pv = wi.ToList().FindIndex(x => x.OpCode == OpCodes.Ldfld && ((FieldReference)x.Operand).Name == "PluginVersion");
  int br = pv; while (wi[br].OpCode != OpCodes.Brfalse) br--;
  var target = (Instruction)wi[br].Operand;
  var il = write.Body.GetILProcessor();
  il.InsertAfter(wi[br], Instruction.Create(OpCodes.Brtrue, target));
  il.InsertAfter(wi[br], Instruction.Create(OpCodes.Ldloc_0));
  // --- reader: before the first `ldarg.0; ldflda _words; call Clear`, insert the conditional read.
  var ri = read.Body.Instructions;
  var readField = (FieldReference)ri.First(x => x.OpCode == OpCodes.Ldfld && ((FieldReference)x.Operand).Name == "Read").Operand;
  var uintVar = (MethodReference)ri.First(x => x.OpCode == OpCodes.Callvirt && ((MethodReference)x.Operand).Name == "UIntVar").Operand;
  int clear = ri.ToList().FindIndex(x => x.OpCode == OpCodes.Call && ((MethodReference)x.Operand).Name == "Clear");
  var skip = ri[clear - 2]; // ldarg.0 of the _words load
  var rl = read.Body.GetILProcessor();
  var seq = new[] {
    Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, simField), Instruction.Create(OpCodes.Isinst, serverType), Instruction.Create(OpCodes.Brfalse, skip),
    Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, simField), Instruction.Create(OpCodes.Callvirt, getTopology), Instruction.Create(OpCodes.Ldc_I4_2), Instruction.Create(OpCodes.Bne_Un, skip),
    Instruction.Create(OpCodes.Ldarg_3), Instruction.Create(OpCodes.Ldarg_1), Instruction.Create(OpCodes.Ldfld, readField), Instruction.Create(OpCodes.Callvirt, uintVar), Instruction.Create(OpCodes.Stind_I4),
  };
  foreach (var ins in seq) rl.InsertBefore(skip, ins);
  write.Body.OptimizeMacros(); read.Body.OptimizeMacros();
  asm.Write(args[2]);
  Console.WriteLine($"patched -> {args[2]} (writer insert after IL idx {br}, reader insert before idx {clear - 2})");
  return;
}
Console.WriteLine("unknown mode");
