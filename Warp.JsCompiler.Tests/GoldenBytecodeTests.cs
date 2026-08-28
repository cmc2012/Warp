using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Warp.JsCompiler;
using Warp.JsCompiler.Api;
using Warp.JsCompiler.Frontend;
using Warp.Testing;
using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class ModuleGoldenTests
{
    [Fact] public void Empty_module() => GoldenAssert.Module("", "/tmp/ecmac-samples/empty.js", "0101164061696F742F656D7074790FA803000000000E000203A401000000010000050008E8022929");
    [Fact] public void Variable_declaration() => GoldenAssert.Module("var x = 1;", "/tmp/var.js", "0102124061696F742F76617202780FA803000000000E000203A4010000000101000700AA03000108E80229B4DF29");
    [Fact] public void Variable_assignment() => GoldenAssert.Module("var x = 1; x = x + 1;", "/tmp/ops.js", "0102124061696F742F6F707302780FA803000000000E000203A4010000000201000A00AA03000108E80229B4E3B49DDF29");
    [Fact] public void Lexical_let_declaration() => GoldenAssert.Module("let x = 1;", "let.js", "0102124061696F742F6C657402780FA803000000000E000203A4010000000101000700AA03000908E80229B4DF29");
    [Fact] public void Lexical_const_declaration() => GoldenAssert.Module("const x = 1;", "const.js", "0102164061696F742F636F6E737402780FA803000000000E000203A4010000000101000700AA03000D08E80229B4DF29");
    [Fact] public void String_literal() => GoldenAssert.Module("var x = 'hello';", "string-hello.js", "0103244061696F742F737472696E672D68656C6C6F02780A68656C6C6F0FA803000000000E000203A4010000000101000B00AA03000108E8022904D6000000DF29");
    [Fact] public void Array_literal() => GoldenAssert.Module("var x = [1, 2];", "array.js", "0102164061696F742F617272617902780FA803000000000E000203A4010000000201000B00AA03000108E80229B4B5260200DF29");
    [Fact] public void Object_literal() => GoldenAssert.Module("var x = { a: 1, b: 2 };", "object.js", "0104184061696F742F6F626A6563740278026102620FA803000000000E000203A4010000000201001300AA03000108E802290BB44CD6000000B54CD7000000DF29");
    [Fact] public void Global_member_write() => GoldenAssert.Module("globalThis.x = 1;", "member.js", "0102184061696F742F6D656D62657202780FA803000000000E000203A401000000020000100008E80229388C000000B443D500000029");
    [Fact] public void Relational_expression() => GoldenAssert.Module("var x = 1 < 2;", "compare.js", "01021A4061696F742F636F6D7061726502780FA803000000000E000203A4010000000201000900AA03000108E80229B4B5A3DF29");
    [Fact] public void Logical_not() => GoldenAssert.Module("var x = !false;", "not.js", "0102124061696F742F6E6F7402780FA803000000000E000203A4010000000101000800AA03000108E802290996DF29");
    [Fact] public void Negative_integer_literal() => GoldenAssert.Module("var x = -42;", "negative.js", "01021C4061696F742F6E6567617469766502780FA803000000000E000203A4010000000101000800AA03000108E80229BBD6DF29");
    [Fact] public void Function_call() => GoldenAssert.Module("var x = foo(1);", "call.js", "0103144061696F742F63616C6C027806666F6F0FA803000000000E000203A4010000000201000D00AA03000108E8022938D6000000B4EDDF29");
    [Fact] public void Throw_statement() => GoldenAssert.Module("throw 1;", "throw.js", "0101164061696F742F7468726F770FA803000000000E000203A401000000010000060008E80229B42F");
    [Fact] public void Undefined_identifier() => GoldenAssert.Module("var x = undefined;", "undefined.js", "01021E4061696F742F756E646566696E656402780FA803000000000E000203A4010000000101000B00AA03000108E802293847000000DF29");
    [Fact] public void While_false() => GoldenAssert.Module("var x = 0; while (false) x = 1;", "while-false.js", "0102224061696F742F7768696C652D66616C736502780FA803000000000E000203A4010000000101000700AA03000108E80229B3DF29");
}

internal static class GoldenAssert
{
    internal static void Module(string source, string fileName, string expectedHex)
        => AssertExact(source, fileName, JavaScriptSourceKind.Module, expectedHex);

    internal static void Script(string source, string fileName, string expectedHex)
        => AssertExact(source, fileName, JavaScriptSourceKind.Script, expectedHex);

    internal static void ReferenceModule(string source, string fileName)
        => Reference(source, fileName, JavaScriptSourceKind.Module);

    internal static void ReferenceScript(string source, string fileName)
        => Reference(source, fileName, JavaScriptSourceKind.Script);

    internal static void ReferenceModuleWithExternalImports(string source, string fileName)
    {
        var referencePath = Environment.GetEnvironmentVariable("WARP_REFERENCE_ECMAC");
        if (string.IsNullOrWhiteSpace(referencePath))
            Assert.Skip("Reference bytecode comparison requires WARP_REFERENCE_ECMAC to name the reference compiler.");
        var expected = CompileWithReference(referencePath, source, fileName, JavaScriptSourceKind.Module);
        Assert.NotNull(expected);
        var graph = new JavaScriptCompiler().CompileModuleGraph(
            new JavaScriptCompilationRequest(source, fileName, JavaScriptSourceKind.Module), new ExternalResolver());
        var actual = graph.Modules[fileName].Bytes;
        var mismatch = FirstMismatch(expected, actual);
        Assert.True(actual.SequenceEqual(expected),
            $"Bytecode differs from the reference compiler at offset {mismatch}. " +
            $"Expected: {Window(expected, mismatch)}. Actual: {Window(actual, mismatch)}.");
    }

    private static void Reference(string source, string fileName, JavaScriptSourceKind kind)
    {
        var referencePath = Environment.GetEnvironmentVariable("WARP_REFERENCE_ECMAC");
        if (string.IsNullOrWhiteSpace(referencePath))
            Assert.Skip("Reference bytecode comparison requires WARP_REFERENCE_ECMAC to name the reference compiler.");
        Assert.True(File.Exists(referencePath), $"Reference compiler does not exist: {referencePath}");
        var expected = CompileWithReference(referencePath, source, fileName, kind, allowFailure: true);
        if (expected is null)
        {
            Assert.Throws<JavaScriptCompilationException>(() => new JavaScriptCompiler()
                .Compile(new JavaScriptCompilationRequest(source, fileName, kind)));
            return;
        }
        var actual = new JavaScriptCompiler()
            .Compile(new JavaScriptCompilationRequest(source, fileName, kind)).Bytes;
        var dumpPath = Environment.GetEnvironmentVariable("WARP_REFERENCE_DUMP");
        if (!string.IsNullOrWhiteSpace(dumpPath))
        {
            var casePath = dumpPath + "." + Path.GetFileNameWithoutExtension(fileName);
            File.WriteAllBytes(casePath + ".reference", expected);
            File.WriteAllBytes(casePath + ".actual", actual.ToArray());
        }
        var mismatch = FirstMismatch(expected, actual);
        Assert.True(actual.SequenceEqual(expected),
            $"Bytecode differs from the reference compiler at offset {mismatch}. " +
            $"Expected: {Window(expected, mismatch)}. Actual: {Window(actual, mismatch)}.");
    }

    private static void AssertExact(string source, string fileName, JavaScriptSourceKind kind, string expectedHex)
    {
        // The checked-in hex is a reproducible recording, not the oracle.
        // When WARP_REFERENCE_ECMAC points to the supplied reference driver,
        // verify that recording before comparing this compiler's output. This
        // detects a bad fixture instead of sending implementation work after
        // an incorrect expected byte stream.
        VerifyFrozenReference(source, fileName, kind, expectedHex);
        var actual = new JavaScriptCompiler()
            .Compile(new JavaScriptCompilationRequest(source, fileName, kind)).Bytes;
        var expected = Convert.FromHexString(expectedHex);
        var mismatch = FirstMismatch(expected, actual);
        Assert.True(actual.SequenceEqual(expected),
            $"Bytecode differs at offset {mismatch}. Expected: {Window(expected, mismatch)}. Actual: {Window(actual, mismatch)}.\n" +
            $"Expected full: {Convert.ToHexString(expected.ToArray())}\nActual full: {Convert.ToHexString(actual.ToArray())}");
    }

    private static readonly ConcurrentDictionary<string, byte[]> ReferenceOutputs = new(StringComparer.Ordinal);

    private static void VerifyFrozenReference(string source, string fileName, JavaScriptSourceKind kind, string expectedHex)
    {
        var referencePath = Environment.GetEnvironmentVariable("WARP_REFERENCE_ECMAC");
        if (string.IsNullOrWhiteSpace(referencePath)) return;
        Assert.True(File.Exists(referencePath), $"Reference compiler does not exist: {referencePath}");

        var key = $"{kind}\0{Path.GetFileName(fileName)}\0{source}";
        var expected = Convert.FromHexString(expectedHex);
        var reference = ReferenceOutputs.GetOrAdd(key, _ =>
            CompileWithReference(referencePath, source, fileName, kind) ??
            throw new InvalidOperationException("Reference compiler unexpectedly rejected a frozen golden source."));
        var mismatch = FirstMismatch(expected, reference);
        Assert.True(reference.SequenceEqual(expected),
            $"Frozen golden differs from the reference compiler at offset {mismatch}. " +
            $"Frozen: {Window(expected, mismatch)}. Reference: {Window(reference, mismatch)}.\n" +
            $"Frozen full: {Convert.ToHexString(expected)}\nReference full: {Convert.ToHexString(reference)}");
    }

    private static byte[]? CompileWithReference(string referencePath, string source, string fileName, JavaScriptSourceKind kind,
        bool allowFailure = false)
    {
        var directory = TestWorkspace.CreateDirectory("warp-reference-golden");
        try
        {
            var input = Path.Combine(directory, Path.GetFileName(fileName));
            var output = Path.Combine(directory, "output.c");
            File.WriteAllText(input, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (kind == JavaScriptSourceKind.Module)
            {
                // Dependency synthesis is only a convenience for successful
                // module compilations.  A syntax error must still reach the
                // common allowFailure path, where the reference driver's
                // rejection is compared with the production compiler's
                // rejection instead of escaping from this test helper.
                JavaScriptProgram parsed;
                try { parsed = new JavaScriptFrontEnd(source, fileName, kind).Parse(); }
                catch (JavaScriptCompilationException) when (allowFailure) { return null; }
                foreach (var import in parsed.StaticImports)
                {
                    var dependency = Path.GetFullPath(Path.Combine(directory, import.Specifier));
                    if (!dependency.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dependency)!);
                    if (!File.Exists(dependency))
                        File.WriteAllText(dependency, "export default 1; export const value = 1;",
                            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
            }

            var start = new ProcessStartInfo(referencePath)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("-s");
            if (kind == JavaScriptSourceKind.Module) start.ArgumentList.Add("-m");
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add(output);
            start.ArgumentList.Add(input);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start reference compiler.");
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                if (allowFailure) return null;
                Assert.Fail($"Reference compiler failed: {standardError}");
            }

            var hex = string.Concat(Regex.Matches(File.ReadAllText(output), @"0x[0-9a-fA-F]{2}")
                .Select(match => match.Value[2..]));
            Assert.NotEmpty(hex);
            return Convert.FromHexString(hex);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static int FirstMismatch(IReadOnlyList<byte> expected, IReadOnlyList<byte> actual)
    {
        var common = Math.Min(expected.Count, actual.Count);
        for (var i = 0; i < common; i++)
            if (expected[i] != actual[i]) return i;
        return expected.Count == actual.Count ? -1 : common;
    }

    private sealed class ExternalResolver : IJavaScriptModuleResolver
    {
        public JavaScriptModuleSource Resolve(string specifier, string referrer) =>
            new(specifier, "", IsExternal: true);
    }

    private static string Window(IReadOnlyList<byte> bytes, int offset)
    {
        var start = Math.Max(0, offset - 8);
        var length = Math.Min(bytes.Count - start, 16);
        return Convert.ToHexString(bytes.Skip(start).Take(length).ToArray());
    }
}
