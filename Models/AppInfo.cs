using System.Reflection;

namespace LocalDiskBrowser.Models
{
    public static class AppInfo
    {
        /// <summary>Version from &lt;Version&gt; in LocalDiskBrowser.csproj (any "+build" suffix removed).</summary>
        public static string Version { get; } =
            (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown")
            .Split('+')[0];
    }
}
