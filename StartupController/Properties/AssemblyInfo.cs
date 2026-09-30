using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

// Kept separate from GeneratedVersionInfo.cs, which tools/Increment-BuildVersion.ps1 overwrites.
[assembly: InternalsVisibleTo("StartupController.Tests")]

// GenerateAssemblyInfo is false, so the SDK no longer emits this for net8.0-windows (it caused the CA1416 noise)
[assembly: SupportedOSPlatform("windows7.0")]
