using System.Reflection;

namespace BLearn.Application;

/// <summary>The running build's version, stamped from git tags at build time (MinVer). Never hardcoded.</summary>
public static class BuildInfo
{
    public static string Version { get; } = Resolve();

    private static string Resolve()
    {
        var info = typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return "0.0.0";
        var plus = info.IndexOf('+');   // drop "+<git sha>" build metadata
        return plus >= 0 ? info[..plus] : info;
    }
}
