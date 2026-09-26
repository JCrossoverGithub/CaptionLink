using System.Diagnostics;

namespace TransGo.Core.Runtime;

/// <summary>
/// Provides the platform-specific environment used to run
/// local TransGo services.
/// </summary>
public interface ILocalServiceRuntime
{
    string GetRepositoryDirectory(
        string relativeDirectory);

    ProcessStartInfo CreateShellStartInfo(
        string command,
        bool redirectOutput);
}
