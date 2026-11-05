using System.Reflection;

namespace Translator.Integration.Tests;

internal static class TestBuildPaths
{
    internal static string ProjectDll(string project)
    {
        string configuration = typeof(TestBuildPaths).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        return Path.GetFullPath(
            $"../../../../../src/{project}/bin/{configuration}/net10.0/{project}.dll",
            AppContext.BaseDirectory);
    }
}
