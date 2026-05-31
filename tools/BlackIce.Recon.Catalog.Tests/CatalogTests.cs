using BlackIce.Recon.Catalog;
using Mono.Cecil;
using Xunit;

public class CatalogTests
{
    // Game assemblies live outside the repo; tests read them in place (analysis-only).
    const string GameManaged =
        @"C:\Program Files (x86)\Steam\steamapps\common\Black Ice\Black Ice_Data\Managed";

    static ModuleDefinition Module(string dll) =>
        ModuleDefinition.ReadModule(System.IO.Path.Combine(GameManaged, dll));

    [Fact]
    public void Finds_at_least_80_PunRPC_methods()
    {
        var rpcs = Catalog.ExtractRpcs(Module("Assembly-CSharp.dll"));
        Assert.True(rpcs.Count >= 80, $"expected >=80 RPCs, found {rpcs.Count}");
        Assert.All(rpcs, r => Assert.False(string.IsNullOrWhiteSpace(r.Method)));
    }

    [Fact]
    public void Extracts_Photon_OperationCode_with_Authenticate()
    {
        var consts = Catalog.ExtractNamedConstants(
            Module("PhotonRealtime.dll"),
            new[] { "OperationCode", "EventCode", "ParameterCode", "ErrorCode" });
        var ops = consts.Where(c => c.Group == "OperationCode").ToList();
        Assert.NotEmpty(ops);
        Assert.Contains(ops, c => c.Name == "Authenticate");
    }
}
