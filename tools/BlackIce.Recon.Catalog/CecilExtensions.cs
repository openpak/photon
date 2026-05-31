using Mono.Cecil;
using Mono.Cecil.Cil;

namespace BlackIce.Recon.Catalog;

internal static class CecilExtensions
{
    /// <summary>All types in a module, including nested types, flattened.</summary>
    public static IEnumerable<TypeDefinition> AllTypes(this ModuleDefinition module)
    {
        foreach (var t in module.Types)
        {
            yield return t;
            foreach (var nested in t.NestedTypes) yield return nested;
        }
    }

    /// <summary>True if the method body references PhotonNetwork.IsMasterClient (authority hint).</summary>
    public static bool ReferencesMasterClient(this MethodDefinition m)
    {
        if (!m.HasBody) return false;
        foreach (var instr in m.Body.Instructions)
            if ((instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt)
                && instr.Operand is MethodReference mr
                && mr.Name.Contains("IsMasterClient"))
                return true;
        return false;
    }
}
