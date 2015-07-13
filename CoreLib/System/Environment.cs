﻿namespace System
{
    using System.Diagnostics.Contracts;

    using Runtime.CompilerServices;

    public static class Environment
    {
        [MethodImplAttribute(MethodImplOptions.Unmanaged)]
        public static extern unsafe int clock_gettime(int type, int* time);

        [MethodImpl(MethodImplOptions.Unmanaged)]
        public static extern void __exit(int exitCode);

        public static string NewLine = "\r\n";

        public static string Space = " ";

        private const long TicksPerMillisecond = 10000;

        private const long TicksPerSecond = TicksPerMillisecond * 1000;

        private const int CLOCK_MONOTONIC = 1;

        public static string CurrentDirectory { get; set; }

        public static int ExitCode { get; set; }

        private static OperatingSystem _os;

        public static int TickCount
        {
            get
            {
                unsafe
                {
                    long time;
                    if (clock_gettime(CLOCK_MONOTONIC, (int*)&time) == 0)
                    {
                        return (int) time >> 32;
                    }

                    throw new Exception();
                }
            }
        }

        public static int ProcessorCount
        {
            get
            {
                return 2;
            }
            set
            {
                throw new NotImplementedException();
            }
        }

        public static OperatingSystem OSVersion
        {
            [System.Security.SecuritySafeCritical]  // auto-generated
            get
            {
                Contract.Ensures(Contract.Result<OperatingSystem>() != null);

                if (_os == null)
                { // We avoid the lock since we don't care if two threads will set this at the same time.
#if PLATFORM_UNIX
                    PlatformID id = PlatformID.Unix;
#else
                    PlatformID id = PlatformID.Win32NT;
#endif // PLATFORM_UNIX

                    var v = new Version(6, 1, 7600);
                    _os = new OperatingSystem(id, v);
                }

                Contract.Assert(_os != null, "m_os != null");
                return _os;
            }
        }
        
        public static bool HasShutdownStarted { get; set; }

        public static bool IsWindows8OrAbove { get; set; }

        public static string GetResourceString(string name)
        {
            return name;
        }

        public static string GetResourceString(string name, string value)
        {
            return name + Space + value;
        }

        public static string GetResourceString(string name, int value)
        {
            return name + Space + value;
        }

        public static string GetResourceString(string name, object value, object value2)
        {
            return name + Space + value + Space + value2;
        }

        public static string GetResourceString(string name, string value, string value2)
        {
            return name + Space + value + Space + value2;
        }

        [MethodImpl(MethodImplOptions.Unmanaged)]
        public static extern void Exit(int exitCode);

        public static void FailFast(string getResourceString, Exception exception)
        {
            throw new NotImplementedException();
        }

        public static string GetEnvironmentVariable(string envVarName)
        {
            return string.Empty;
        }
    }
}
