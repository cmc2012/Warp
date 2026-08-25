using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>One independently compiled module golden per assertion from the reference worker suite (bignum excluded).</summary>
public sealed class ReferenceWorkerGoldenTests
{
    public static IEnumerable<object[]> Cases
    {
        get
        {
            yield return new object[] { "worker_assert_001", "assert(ev.num, counter);", "/tmp/ecma-unit/worker/assert-001.js", "0105204061696f742f6173736572742d3030310c617373657274046576066e756d0e636f756e7465720fa803000000000e000203a4010000000300001a0008e8022938d500000038d600000041d700000038d8000000ee29" };
            yield return new object[] { "worker_assert_002", "assert(buf[2], 10);", "/tmp/ecma-unit/worker/assert-002.js", "0103204061696f742f6173736572742d3030320c617373657274066275660fa803000000000e000203a401000000030000140008e8022938d500000038d6000000b547bb0aee29" };
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Emits_exact_module_bytecode(string _, string source, string fileName, string expectedHex)
        => GoldenAssert.Module(source, fileName, expectedHex);
}
