using Mono.Cecil;
using Mono.Cecil.Cil;

namespace BlackIce.Recon.Catalog;

public static class Catalog
{
    /// <summary>All methods decorated with the PUN [PunRPC] attribute.</summary>
    public static List<RpcEntry> ExtractRpcs(ModuleDefinition module)
    {
        var result = new List<RpcEntry>();
        foreach (var type in module.AllTypes())
            foreach (var method in type.Methods)
                if (method.CustomAttributes.Any(a => a.AttributeType.Name == "PunRPC"))
                    result.Add(new RpcEntry(
                        type.FullName,
                        method.Name,
                        method.Parameters.Select(p => p.ParameterType.Name).ToArray(),
                        method.ReferencesMasterClient()));
        return result;
    }

    /// <summary>
    /// Extracts literal members (enum members and `const` fields both qualify) from the named
    /// types. Works for Photon's OperationCode/EventCode/ParameterCode/ErrorCode (const-byte
    /// classes) and StatusCode (enum) alike.
    /// </summary>
    public static List<ConstantEntry> ExtractNamedConstants(ModuleDefinition module, string[] groupTypeNames)
    {
        var wanted = new HashSet<string>(groupTypeNames);
        var result = new List<ConstantEntry>();
        foreach (var type in module.AllTypes())
        {
            if (!wanted.Contains(type.Name)) continue;
            foreach (var field in type.Fields)
                if (field.IsLiteral && field.HasConstant && field.Constant is not null)
                    result.Add(new ConstantEntry(type.Name, field.Name, field.Constant));
        }
        return result;
    }

    /// <summary>OnPhotonSerializeView implementations and their stream call order (payload layout).</summary>
    public static List<SerializeViewEntry> ExtractSerializeViews(ModuleDefinition module)
    {
        var result = new List<SerializeViewEntry>();
        foreach (var type in module.AllTypes())
            foreach (var m in type.Methods)
            {
                if (m.Name != "OnPhotonSerializeView" || !m.HasBody) continue;
                var calls = new List<string>();
                foreach (var instr in m.Body.Instructions)
                    if ((instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt)
                        && instr.Operand is MethodReference mr
                        && (mr.Name is "SendNext" or "ReceiveNext" or "Serialize"))
                        calls.Add(mr.Name);
                result.Add(new SerializeViewEntry(type.FullName, calls.ToArray()));
            }
        return result;
    }

    /// <summary>Prefab name string literals passed to PhotonNetwork.Instantiate / InstantiateRoomObject.</summary>
    public static List<InstantiateEntry> ExtractInstantiations(ModuleDefinition module)
    {
        var result = new List<InstantiateEntry>();
        foreach (var type in module.AllTypes())
            foreach (var m in type.Methods)
            {
                if (!m.HasBody) continue;
                Instruction? prevStr = null;
                foreach (var instr in m.Body.Instructions)
                {
                    if (instr.OpCode == OpCodes.Ldstr) prevStr = instr;
                    if ((instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt)
                        && instr.Operand is MethodReference mr
                        && (mr.Name is "Instantiate" or "InstantiateRoomObject")
                        && mr.DeclaringType.Name == "PhotonNetwork"
                        && prevStr?.Operand is string prefab)
                        result.Add(new InstantiateEntry(type.FullName, m.Name, prefab));
                }
            }
        return result;
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        // args[0] = game Managed dir, args[1] = output dir
        var managed = args.Length > 0 ? args[0]
            : @"C:\Program Files (x86)\Steam\steamapps\common\Black Ice\Black Ice_Data\Managed";
        var outDir = args.Length > 1 ? args[1]
            : Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "protocol", "generated"));
        Directory.CreateDirectory(outDir);

        var game = ModuleDefinition.ReadModule(Path.Combine(managed, "Assembly-CSharp.dll"));
        var realtime = ModuleDefinition.ReadModule(Path.Combine(managed, "PhotonRealtime.dll"));

        var rpcs = Catalog.ExtractRpcs(game);
        var consts = Catalog.ExtractNamedConstants(realtime,
            new[] { "OperationCode", "EventCode", "ParameterCode", "ErrorCode" });
        var views = Catalog.ExtractSerializeViews(game);
        var insts = Catalog.ExtractInstantiations(game);

        WriteCsv(Path.Combine(outDir, "rpcs.csv"),
            "DeclaringType,Method,Parameters,ReferencesMasterClient",
            rpcs.Select(r => $"{r.DeclaringType},{r.Method},{string.Join('|', r.Parameters)},{r.ReferencesMasterClient}"));
        WriteCsv(Path.Combine(outDir, "photon_constants.csv"),
            "Group,Name,Value",
            consts.Select(c => $"{c.Group},{c.Name},{c.Value}"));
        WriteCsv(Path.Combine(outDir, "serialize_views.csv"),
            "DeclaringType,StreamCallOrder",
            views.Select(v => $"{v.DeclaringType},{string.Join('|', v.StreamCallOrder)}"));
        WriteCsv(Path.Combine(outDir, "instantiations.csv"),
            "DeclaringType,Method,PrefabName",
            insts.Select(i => $"{i.DeclaringType},{i.Method},{i.PrefabName}"));

        Console.WriteLine($"RPCs={rpcs.Count} Constants={consts.Count} SerializeViews={views.Count} Instantiations={insts.Count}");
        Console.WriteLine($"Wrote tables to {outDir}");
        return 0;
    }

    static void WriteCsv(string path, string header, IEnumerable<string> rows)
        => File.WriteAllLines(path, new[] { header }.Concat(rows));
}
