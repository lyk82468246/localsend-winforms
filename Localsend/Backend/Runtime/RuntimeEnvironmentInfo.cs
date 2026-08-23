using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Localsend.Backend.Runtime
{
    /// <summary>
    /// Stable names used by the UI and diagnostics. The numeric values match
    /// the Windows SYSTEM_INFO processor architecture constants where useful.
    /// </summary>
    public enum CpuArchitecture
    {
        Unknown = -1,
        X86 = 0,
        Arm = 5,
        Itanium = 6,
        X64 = 9,
        Arm64 = 12
    }

    /// <summary>
    /// Read-only snapshot of the process and operating environment.
    /// This class deliberately contains no TLS probing; it is safe to collect
    /// on both the Compact Framework and the desktop CLR.
    /// </summary>
    public sealed class RuntimeEnvironmentInfo
    {
        public bool IsWindowsCe { get; private set; }
        public string OperatingSystem { get; private set; }
        public string OperatingSystemVersion { get; private set; }
        public int OperatingSystemBuild { get; private set; }
        public string ProcessArchitecture { get; private set; }
        public string NativeArchitecture { get; private set; }
        public int PointerBits { get; private set; }
        public string RuntimeVersion { get; private set; }
        public string FrameworkKind { get; private set; }
        public string ApplicationVersion { get; private set; }

        private RuntimeEnvironmentInfo() { }

        public static RuntimeEnvironmentInfo Capture()
        {
            RuntimeEnvironmentInfo result = new RuntimeEnvironmentInfo();
            result.IsWindowsCe = IsWindowsCePlatform();
            result.PointerBits = IntPtr.Size * 8;
            result.RuntimeVersion = SafeRuntimeVersion();
            result.FrameworkKind = result.IsWindowsCe ? "Compact Framework" : ".NET Framework/CLR";
            result.ApplicationVersion = SafeApplicationVersion();

            Version osVersion = SafeEnvironmentVersion();
            if (!result.IsWindowsCe)
            {
                Version rtlVersion = NativePlatform.TryGetWindowsVersion();
                if (rtlVersion != null) osVersion = rtlVersion;
            }

            if (osVersion == null) osVersion = new Version(0, 0);
            result.OperatingSystemVersion = osVersion.ToString();
            result.OperatingSystemBuild = osVersion.Build < 0 ? 0 : osVersion.Build;
            result.OperatingSystem = GetOperatingSystemName(result.IsWindowsCe, osVersion);

            CpuArchitecture native = NativePlatform.TryGetNativeArchitecture(result.IsWindowsCe);
            if (native == CpuArchitecture.Unknown)
                native = GuessProcessArchitecture(result.PointerBits, result.IsWindowsCe);

            CpuArchitecture process = result.IsWindowsCe
                ? native
                : GuessProcessArchitecture(result.PointerBits, false);

            result.NativeArchitecture = ArchitectureName(native);
            result.ProcessArchitecture = ArchitectureName(process);
            return result;
        }

        public string ToDiagnosticString()
        {
            return OperatingSystem + " " + OperatingSystemVersion
                + " (build " + OperatingSystemBuild + ")"
                + "; process=" + ProcessArchitecture
                + "; native=" + NativeArchitecture
                + "; " + PointerBits + "-bit"
                + "; " + FrameworkKind + " " + RuntimeVersion;
        }

        public static string ArchitectureName(CpuArchitecture architecture)
        {
            switch (architecture)
            {
                case CpuArchitecture.X86: return "x86";
                case CpuArchitecture.X64: return "x64";
                case CpuArchitecture.Arm: return "ARM";
                case CpuArchitecture.Arm64: return "ARM64";
                case CpuArchitecture.Itanium: return "IA64";
                default: return "Unknown";
            }
        }

        private static bool IsWindowsCePlatform()
        {
            try { return Environment.OSVersion.Platform == PlatformID.WinCE; }
            catch { return false; }
        }

        private static Version SafeEnvironmentVersion()
        {
            try { return Environment.OSVersion.Version; }
            catch { return null; }
        }

        private static string SafeRuntimeVersion()
        {
            try { return Environment.Version.ToString(); }
            catch { return "unknown"; }
        }

        private static string SafeApplicationVersion()
        {
            try
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "unknown" : v.ToString();
            }
            catch { return "unknown"; }
        }

        private static CpuArchitecture GuessProcessArchitecture(int pointerBits, bool isCe)
        {
            if (isCe) return CpuArchitecture.Unknown;
            return pointerBits >= 64 ? CpuArchitecture.X64 : CpuArchitecture.X86;
        }

        private static string GetOperatingSystemName(bool isCe, Version version)
        {
            if (isCe) return "Windows CE / Windows Mobile";
            if (version.Major == 10 && version.Build >= 22000) return "Windows 11";
            if (version.Major == 10) return "Windows 10";
            if (version.Major == 6 && version.Minor == 3) return "Windows 8.1";
            if (version.Major == 6 && version.Minor == 2) return "Windows 8";
            if (version.Major == 6 && version.Minor == 1) return "Windows 7";
            if (version.Major == 6 && version.Minor == 0) return "Windows Vista";
            if (version.Major == 5 && version.Minor == 2) return "Windows XP/Server 2003";
            if (version.Major == 5 && version.Minor == 1) return "Windows XP";
            return "Windows";
        }
    }

    internal static class NativePlatform
    {
        private const ushort PROCESSOR_ARCHITECTURE_INTEL = 0;
        private const ushort PROCESSOR_ARCHITECTURE_ARM = 5;
        private const ushort PROCESSOR_ARCHITECTURE_IA64 = 6;
        private const ushort PROCESSOR_ARCHITECTURE_AMD64 = 9;
        private const ushort PROCESSOR_ARCHITECTURE_ARM64 = 12;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OsVersionInfoEx
        {
            public int OSVersionInfoSize;
            public int MajorVersion;
            public int MinorVersion;
            public int BuildNumber;
            public int PlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string CSDVersion;
            public ushort ServicePackMajor;
            public ushort ServicePackMinor;
            public ushort SuiteMask;
            public byte ProductType;
            public byte Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemInfo
        {
            public ushort ProcessorArchitecture;
            public ushort Reserved;
            public uint PageSize;
            public IntPtr MinimumApplicationAddress;
            public IntPtr MaximumApplicationAddress;
            public IntPtr ActiveProcessorMask;
            public uint NumberOfProcessors;
            public uint ProcessorType;
            public uint AllocationGranularity;
            public ushort ProcessorLevel;
            public ushort ProcessorRevision;
        }

        [DllImport("ntdll.dll", EntryPoint = "RtlGetVersion", CharSet = CharSet.Unicode)]
        private static extern int RtlGetVersion(ref OsVersionInfoEx info);

        [DllImport("kernel32.dll", EntryPoint = "GetNativeSystemInfo")]
        private static extern void GetNativeSystemInfo(ref SystemInfo info);

        [DllImport("kernel32.dll", EntryPoint = "GetSystemInfo")]
        private static extern void GetSystemInfo(ref SystemInfo info);

        [DllImport("coredll.dll", EntryPoint = "GetSystemInfo")]
        private static extern void GetCeSystemInfo(ref SystemInfo info);

        public static Version TryGetWindowsVersion()
        {
            try
            {
                OsVersionInfoEx info = new OsVersionInfoEx();
                info.OSVersionInfoSize = Marshal.SizeOf(typeof(OsVersionInfoEx));
                if (RtlGetVersion(ref info) == 0)
                    return new Version(info.MajorVersion, info.MinorVersion, info.BuildNumber);
            }
            catch { }
            return null;
        }

        public static CpuArchitecture TryGetNativeArchitecture(bool isCe)
        {
            try
            {
                SystemInfo info = new SystemInfo();
                if (isCe) GetCeSystemInfo(ref info);
                else GetNativeSystemInfo(ref info);
                return ConvertArchitecture(info.ProcessorArchitecture);
            }
            catch
            {
                if (!isCe)
                {
                    try
                    {
                        SystemInfo info = new SystemInfo();
                        GetSystemInfo(ref info);
                        return ConvertArchitecture(info.ProcessorArchitecture);
                    }
                    catch { }
                }
            }
            return CpuArchitecture.Unknown;
        }

        private static CpuArchitecture ConvertArchitecture(ushort value)
        {
            switch (value)
            {
                case PROCESSOR_ARCHITECTURE_INTEL: return CpuArchitecture.X86;
                case PROCESSOR_ARCHITECTURE_ARM: return CpuArchitecture.Arm;
                case PROCESSOR_ARCHITECTURE_IA64: return CpuArchitecture.Itanium;
                case PROCESSOR_ARCHITECTURE_AMD64: return CpuArchitecture.X64;
                case PROCESSOR_ARCHITECTURE_ARM64: return CpuArchitecture.Arm64;
                default: return CpuArchitecture.Unknown;
            }
        }
    }
}
