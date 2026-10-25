using System.Runtime.CompilerServices;
using Tideline.Data.Security;

namespace Tideline.Cli.Tests;

/// <summary>Every test in this assembly shares one fixed account number key, so the hashes agree. A real run reads its key from a file.</summary>
internal static class TestKeys
{
    [ModuleInitializer]
    internal static void Init() => AccountNumbers.ConfigureOnce(() => new AccountNumberKeys(Enumerable.Range(1, 64).Select(i => (byte)i).ToArray()));
}
