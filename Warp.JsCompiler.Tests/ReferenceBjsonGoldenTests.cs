using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>One independently compiled module golden per assertion from the reference bjson suite (bignum excluded).</summary>
public sealed class ReferenceBjsonGoldenTests
{
    public static IEnumerable<object[]> Cases
    {
        get
        {
            yield return new object[] { "bjson_assert_001", "assert(false);", "/tmp/ecma-unit/bjson/assert-001.js", "0102204061696f742f6173736572742d3030310c6173736572740fa803000000000e000203a4010000000200000c0008e8022938d500000009ed29" };
            yield return new object[] { "bjson_assert_002", "assert(array[i].next, array[(i + 1) % n]);", "/tmp/ecma-unit/bjson/assert-002.js", "0105204061696f742f6173736572742d3030320c6173736572740a61727261790269026e0fa803000000000e000203a4010000000500002e0008e8022938d500000038d600000038d700000047416c00000038d600000038d7000000b49d38d80000009c47ee29" };
            yield return new object[] { "bjson_assert_003", "assert(array[i].idx, i);", "/tmp/ecma-unit/bjson/assert-003.js", "0105204061696f742f6173736572742d3030330c6173736572740a61727261790269066964780fa803000000000e000203a401000000030000200008e8022938d500000038d600000038d70000004741d800000038d7000000ee29" };
            yield return new object[] { "bjson_assert_004", "assert(array[i].typed_array.buffer, array_buffer);", "/tmp/ecma-unit/bjson/assert-004.js", "0107204061696f742f6173736572742d3030340c6173736572740a617272617902691674797065645f61727261790c6275666665721861727261795f6275666665720fa803000000000e000203a401000000030000250008e8022938d500000038d600000038d70000004741d800000041d900000038da000000ee29" };
            yield return new object[] { "bjson_assert_005", "assert(array[i].typed_array.length, 1);", "/tmp/ecma-unit/bjson/assert-005.js", "0105204061696f742f6173736572742d3030350c6173736572740a617272617902691674797065645f61727261790fa803000000000e000203a4010000000300001d0008e8022938d500000038d600000038d70000004741d8000000e7b4ee29" };
            yield return new object[] { "bjson_assert_006", "assert(array[i].typed_array.byteOffset, i);", "/tmp/ecma-unit/bjson/assert-006.js", "0106204061696f742f6173736572742d3030360c6173736572740a617272617902691674797065645f617272617914627974654f66667365740fa803000000000e000203a401000000030000250008e8022938d500000038d600000038d70000004741d800000041d900000038d7000000ee29" };
            yield return new object[] { "bjson_assert_007", "assert(false);", "/tmp/ecma-unit/bjson/assert-007.js", "0102204061696f742f6173736572742d3030370c6173736572740fa803000000000e000203a4010000000200000c0008e8022938d500000009ed29" };
            yield return new object[] { "bjson_assert_008", "assert(e instanceof TypeError);", "/tmp/ecma-unit/bjson/assert-008.js", "0103204061696f742f6173736572742d3030380c61737365727402650fa803000000000e000203a401000000030000160008e8022938d500000038d600000038c3000000a7ed29" };
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Emits_exact_module_bytecode(string _, string source, string fileName, string expectedHex)
        => GoldenAssert.Module(source, fileName, expectedHex);
}
