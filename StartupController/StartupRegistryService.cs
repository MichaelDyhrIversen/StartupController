using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StartupController
{
    public class StartupRegistryService
    {
        // Registry paths
        private const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string STARTUP_APPROVED_KEY = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string APP_ORDER_KEY = @"Software\StartupController";
        private const string ORDER_VALUE = "StartupOrder";
        private const string STARTUP_CONTROLLER_NAME = "StartupController";


        // Fetch all startup programs (enabled and disabled)
        public List<StartupProgram> GetStartupPrograms()
        {
            var programs = new List<StartupProgram>();

            using (var runKey = Registry.CurrentUser.OpenSubKey(RUN_KEY, false))
            using (var approvedKey = Registry.CurrentUser.OpenSubKey(STARTUP_APPROVED_KEY, false))
            {
                if (runKey == null || approvedKey == null)
                    return programs;

                foreach (var name in runKey.GetValueNames())
                {
                    var path = runKey.GetValue(name)?.ToString() ?? "";
                    var enabled = IsProgramEnabled(approvedKey, name);
                    if(!enabled)
                        programs.Add(new StartupProgram
                        {
                            Name = name,
                            Path = path,
                            Enabled = enabled,
                            Description = "" // Optionally fetch description from file or elsewhere
                        });
                }
            }

            // Apply custom order if available
            var ordered = ApplyCustomOrder(programs);
            return ordered;
        }

        // Check if a program is enabled in StartupApproved
        private bool IsProgramEnabled(RegistryKey approvedKey, string name)
        {
            var value = approvedKey.GetValue(name) as byte[];
            // Enabled: 0x02 0x00 0x00 0x00..., Disabled: 0x03 0x00 0x00 0x00...
            return value != null && value.Length > 0 && value[0] == 0x02;
        }

        // Enable or disable a startup program
        public void SetProgramEnabled(string name, bool enabled)
        {
            using (var approvedKey = Registry.CurrentUser.OpenSubKey(STARTUP_APPROVED_KEY, true))
            {
                if (approvedKey == null) return;
                var value = approvedKey.GetValue(name) as byte[];
                if (value == null || value.Length == 0) return;
                value[0] = enabled ? (byte)0x02 : (byte)0x03;
                approvedKey.SetValue(name, value, RegistryValueKind.Binary);
            }
        }

        // Save the custom order of startup programs
        public void SaveStartupOrder(List<string> orderedNames)
        {
            using (var appKey = Registry.CurrentUser.CreateSubKey(APP_ORDER_KEY))
            {
                if (appKey == null) return;
                var orderString = string.Join(";", orderedNames);
                appKey.SetValue(ORDER_VALUE, orderString, RegistryValueKind.String);
            }
        }

        // Load the custom order of startup programs
        public List<string> LoadStartupOrder()
        {
            using (var appKey = Registry.CurrentUser.OpenSubKey(APP_ORDER_KEY, false))
            {
                if (appKey == null) return new List<string>();
                var orderString = appKey.GetValue(ORDER_VALUE) as string;
                return string.IsNullOrEmpty(orderString)
                    ? new List<string>()
                    : orderString.Split(';').ToList();
            }
        }

        // Apply custom order to the list of programs
        private List<StartupProgram> ApplyCustomOrder(List<StartupProgram> programs)
        {
            var order = LoadStartupOrder();
            if (order.Count == 0) return programs;

            var ordered = programs.OrderBy(p =>
            {
                var idx = order.IndexOf(p.Name);
                return idx >= 0 ? idx : int.MaxValue;
            }).ToList();
            for(int i = 0; i < order.Count; i++)
            {
                ordered[i].Enabled = true;
            }
            return ordered;
        }

        public void AddThisApplicationToStartup(string exePath)
        {
            // Registry key for current user startup
            using (var key = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
            {
                if (key != null)
                {
                    // Always quote the path in case it contains spaces
                    key.SetValue(STARTUP_CONTROLLER_NAME, $"\"{exePath}\" --launch", RegistryValueKind.String);
                }
            }
        }

        public void RemoveThisApplicationFromStartup()
        {
            // Registry key for current user startup
            using (var key = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
            {
                if (key != null)
                {
                    key.DeleteValue(STARTUP_CONTROLLER_NAME, false);
                }
            }
        }
    }
}
