using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>Independent message-shape units from the reference worker module.</summary>
public sealed class ReferenceWorkerModuleGoldenTests
{
    [Fact] public void Worker_message_001() => GoldenAssert.Module("parent.postMessage({ type: \"done\" });", "/tmp/ecma-unit/worker-module/message-001.js", "0104224061696f742f6d6573736167652d3030310c706172656e7416706f73744d65737361676508747970650fa803000000000e000203a4010000000400001d0008e8022938d500000042d60000000b046b0000004cd700000024010029");
    [Fact] public void Worker_message_002() => GoldenAssert.Module("parent.postMessage({ type: \"sab_done\", buf: ev.buf });", "/tmp/ecma-unit/worker-module/message-002.js", "0107224061696f742f6d6573736167652d3030320c706172656e7416706f73744d657373616765107361625f646f6e650874797065046576066275660fa803000000000e000203a4010000000400002c0008e8022938d500000042d60000000b04d70000004cd800000038d900000041da0000004cda00000024010029");
    [Fact] public void Worker_message_003() => GoldenAssert.Module("parent.postMessage({ type: \"num\", num: i });", "/tmp/ecma-unit/worker-module/message-003.js", "0106224061696f742f6d6573736167652d3030330c706172656e7416706f73744d657373616765066e756d087479706502690fa803000000000e000203a401000000040000270008e8022938d500000042d60000000b04d70000004cd800000038d90000004cd700000024010029");
}
