using Warp.JsCompiler.Assembly;
using Warp.JsCompiler.Encoding;
using Warp.JsCompiler.ObjectFormat;
using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class EncodedAssemblyObjectPassTests
{
    [Fact]
    public void Encodes_adapts_and_writes_atom_bearing_script_function()
    {
        var function = new BytecodeAssemblyFunction(
            new BytecodeAssemblyFunctionId(0),
            BytecodeAssemblyAtom.Predefined(BytecodeTargetAbi.EvalFunctionAtom),
            [
                new(TargetOpcodeCatalog.Get("get_var"), new BytecodeAssemblyAtomReferenceOperand()),
                new(TargetOpcodeCatalog.Get("return")),
            ],
            new BytecodeAssemblyFunctionMetadata(),
            AtomRelocations: [new(0, BytecodeAssemblyAtom.Named("value"))]);
        var assembly = new BytecodeAssemblyProgram(function.Id, [function]);

        var encoded = new BytecodeAssemblyEncoder().Encode(assembly);
        var value = new EncodedAssemblyObjectPass().Run(encoded);
        var bytes = new BytecodeObjectWriter().Write(value);

        Assert.Equal("01010A76616C75650E020200A401000000010000060038D400000028", Convert.ToHexString(bytes));
    }

    [Fact]
    public void Converts_function_metadata_closures_constants_and_module_tables()
    {
        var child = new BytecodeAssemblyFunction(
            new BytecodeAssemblyFunctionId(1), BytecodeAssemblyAtom.Named("child"),
            [new(TargetOpcodeCatalog.Get("return_undef"))],
            new BytecodeAssemblyFunctionMetadata(MaximumStackSize: 1));
        var entry = new BytecodeAssemblyFunction(
            new BytecodeAssemblyFunctionId(0), BytecodeAssemblyAtom.Predefined(BytecodeTargetAbi.EvalFunctionAtom),
            [new(TargetOpcodeCatalog.Get("return_undef"))],
            new BytecodeAssemblyFunctionMetadata(
                ArgumentCount: 1, DefinedArgumentCount: 1, MaximumStackSize: 2,
                NeedsHomeObject: true, Kind: BytecodeAssemblyFunctionKind.Async,
                Locals: [new(BytecodeAssemblyAtom.Named("arg"), IsLexical: true, ScopeLevel: 1)],
                Closures: [new(BytecodeAssemblyAtom.Named("outer"), 2, IsLocal: false, IsConst: true)]),
            Constants:
            [
                new BytecodeAssemblyNumberConstant(new BytecodeAssemblyConstantId(0), 1.5),
                new BytecodeAssemblyStringConstant(new BytecodeAssemblyConstantId(1), "text"),
                new BytecodeAssemblyFunctionConstant(new BytecodeAssemblyConstantId(2), child.Id),
                new BytecodeAssemblyTemplateConstant(new BytecodeAssemblyConstantId(3), ["cooked"], ["raw"]),
            ]);
        var module = new BytecodeAssemblyModuleMetadata(
            BytecodeAssemblyAtom.Named("main"),
            [BytecodeAssemblyAtom.Named("dep")],
            [new BytecodeAssemblyIndirectExport(0, BytecodeAssemblyAtom.Named("source"), BytecodeAssemblyAtom.Named("exported"))],
            [new(0)],
            [new(4, BytecodeAssemblyAtom.Named("default"), 0)]);
        var program = new BytecodeAssemblyProgram(entry.Id, [entry, child], module);

        var encoded = new BytecodeAssemblyEncoder().Encode(program);
        var adapted = Assert.IsType<BytecodeModuleValue>(new EncodedAssemblyObjectPass().Run(encoded));
        var function = adapted.Function;

        Assert.Equal(BytecodeObjectFunctionKind.Async, function.Kind);
        Assert.True(function.NeedsHomeObject);
        Assert.Single(function.Variables!);
        Assert.Single(function.Closures!);
        Assert.Collection(function.Constants!,
            value => Assert.IsType<BytecodeFloatValue>(value),
            value => Assert.IsType<BytecodeStringValue>(value),
            value => Assert.IsType<IrFunctionObject>(value),
            value => Assert.IsType<BytecodeTemplateValue>(value));
        Assert.Single(adapted.RequiredModules!);
        Assert.Single(adapted.Exports!);
        Assert.Single(adapted.StarExports!);
        Assert.Single(adapted.Imports!);
        Assert.NotEmpty(new BytecodeObjectWriter().Write(adapted));
    }
}
