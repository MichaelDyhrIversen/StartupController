using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

// Kept separate from GeneratedVersionInfo.cs, which tools/Increment-BuildVersion.ps1 overwrites.
[assembly: InternalsVisibleTo("StartupController.Tests")]

// GenerateAssemblyInfo is false, so the SDK doesn't emit this for net10.0-windows. Windows 10 1607 is the .NET 10 minimum.
[assembly: SupportedOSPlatform("windows10.0.14393")]
