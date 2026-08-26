namespace Warp.Lsp;

/// <summary>Entry point used by the standalone server and the <c>warp lsp</c> command.</summary>
public static class LspHost
{
    public static Task RunAsync(CancellationToken cancellationToken)
    {
        var server = new JsonRpcServer(Console.OpenStandardInput(), Console.OpenStandardOutput());
        return server.RunAsync(cancellationToken);
    }
}
