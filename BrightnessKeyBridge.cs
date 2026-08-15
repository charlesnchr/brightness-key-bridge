using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace BrightnessKeyBridge
{
    internal static class Program
    {
        private const string MutexName = "Local\\BrightnessKeyBridge-6D1CF39E-8DA2-43F8-9257-A7BEA28576D5";

        [STAThread]
        private static void Main()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                try
                {
                    using (BridgeWindow window = new BridgeWindow())
                    {
                        Logger.Write("Started standalone DDC/CI bridge (HID usages 0x006F/0x0070; step 5%).");
                        Application.Run(window);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Write("Fatal error: " + ex);
                }
            }
        }
    }

    internal sealed class BridgeWindow : Form
    {
        private const int WmInput = 0x00FF;
        private const uint RidInput = 0x10000003;
        private const uint RidiPreparsedData = 0x20000005;
        private const uint RimTypeHid = 2;
        private const uint RidevInputSink = 0x00000100;
        private const uint RidevDevNotify = 0x00002000;
        private const ushort ConsumerUsagePage = 0x000C;
        private const ushort ConsumerControlUsage = 0x0001;
        private const ushort BrightnessUpUsage = 0x006F;
        private const ushort BrightnessDownUsage = 0x0070;
        private const int HidpStatusSuccess = 0x00110000;
        private const int BrightnessStep = 5;

        private readonly BlockingCollection<int> commandQueue = new BlockingCollection<int>();
        private readonly Dictionary<IntPtr, HashSet<ushort>> pressedByDevice = new Dictionary<IntPtr, HashSet<ushort>>();
        private readonly Dictionary<ushort, DateTime> lastTrigger = new Dictionary<ushort, DateTime>();
        private readonly Thread commandThread;
        private int diagnosticReportsRemaining = 30;
        private bool shuttingDown;

        public BridgeWindow()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            WindowState = FormWindowState.Minimized;
            Opacity = 0;
            StartPosition = FormStartPosition.Manual;
            Location = new System.Drawing.Point(-32000, -32000);
            ClientSize = new System.Drawing.Size(1, 1);
            Text = "Brightness Key Bridge";

            commandThread = new Thread(ProcessCommands);
            commandThread.IsBackground = true;
            commandThread.Name = "DDC/CI brightness command worker";
            commandThread.Start();

            IntPtr unused = Handle;
            RegisterForConsumerControl();
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }

        private void RegisterForConsumerControl()
        {
            RawInputDevice[] devices = new RawInputDevice[1];
            devices[0].UsagePage = ConsumerUsagePage;
            devices[0].Usage = ConsumerControlUsage;
            devices[0].Flags = RidevInputSink | RidevDevNotify;
            devices[0].Target = Handle;

            if (!NativeMethods.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf(typeof(RawInputDevice))))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not register for consumer-control raw input.");
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmInput)
            {
                try
                {
                    ProcessRawInput(message.LParam);
                }
                catch (Exception ex)
                {
                    Logger.Write("Raw-input error: " + ex.Message);
                }
            }

            base.WndProc(ref message);
        }

        private void ProcessRawInput(IntPtr rawInputHandle)
        {
            uint size = 0;
            uint headerSize = (uint)Marshal.SizeOf(typeof(RawInputHeader));
            uint result = NativeMethods.GetRawInputData(rawInputHandle, RidInput, IntPtr.Zero, ref size, headerSize);
            if (result == UInt32.MaxValue || size < headerSize + 8)
            {
                return;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                uint readSize = size;
                result = NativeMethods.GetRawInputData(rawInputHandle, RidInput, buffer, ref readSize, headerSize);
                if (result == UInt32.MaxValue || result != size)
                {
                    return;
                }

                RawInputHeader header = (RawInputHeader)Marshal.PtrToStructure(buffer, typeof(RawInputHeader));
                if (header.Type != RimTypeHid)
                {
                    return;
                }

                IntPtr hid = IntPtr.Add(buffer, (int)headerSize);
                uint reportSize = unchecked((uint)Marshal.ReadInt32(hid, 0));
                uint reportCount = unchecked((uint)Marshal.ReadInt32(hid, 4));
                if (reportSize == 0 || reportCount == 0 || reportSize > 4096 || reportCount > 128)
                {
                    return;
                }

                IntPtr reportData = IntPtr.Add(hid, 8);
                for (uint index = 0; index < reportCount; index++)
                {
                    IntPtr report = IntPtr.Add(reportData, checked((int)(index * reportSize)));
                    ProcessHidReport(header.Device, report, reportSize);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private void ProcessHidReport(IntPtr device, IntPtr report, uint reportSize)
        {
            uint preparsedSize = 0;
            uint infoResult = NativeMethods.GetRawInputDeviceInfo(device, RidiPreparsedData, IntPtr.Zero, ref preparsedSize);
            if (infoResult == UInt32.MaxValue || preparsedSize == 0)
            {
                return;
            }

            IntPtr preparsed = Marshal.AllocHGlobal((int)preparsedSize);
            try
            {
                uint actualSize = preparsedSize;
                infoResult = NativeMethods.GetRawInputDeviceInfo(device, RidiPreparsedData, preparsed, ref actualSize);
                if (infoResult == UInt32.MaxValue)
                {
                    return;
                }

                uint maxUsages = NativeMethods.HidP_MaxUsageListLength(HidpReportType.Input, ConsumerUsagePage, preparsed);
                if (maxUsages == 0 || maxUsages > 1024)
                {
                    maxUsages = 32;
                }

                ushort[] usages = new ushort[maxUsages];
                uint usageCount = maxUsages;
                int status = NativeMethods.HidP_GetUsages(
                    HidpReportType.Input,
                    ConsumerUsagePage,
                    0,
                    usages,
                    ref usageCount,
                    preparsed,
                    report,
                    reportSize);

                if (diagnosticReportsRemaining > 0)
                {
                    diagnosticReportsRemaining--;
                    Logger.Write("Consumer report: status=0x" + status.ToString("X8", CultureInfo.InvariantCulture)
                        + ", usages=" + FormatUsages(usages, usageCount)
                        + ", bytes=" + FormatBytes(report, reportSize));
                }

                if (status != HidpStatusSuccess)
                {
                    return;
                }

                HashSet<ushort> current = new HashSet<ushort>();
                for (uint index = 0; index < usageCount && index < usages.Length; index++)
                {
                    current.Add(usages[index]);
                }

                HashSet<ushort> previous;
                if (!pressedByDevice.TryGetValue(device, out previous))
                {
                    previous = new HashSet<ushort>();
                }

                HandleBrightnessUsage(device, BrightnessDownUsage, current, previous, -BrightnessStep);
                HandleBrightnessUsage(device, BrightnessUpUsage, current, previous, BrightnessStep);
                pressedByDevice[device] = current;
            }
            finally
            {
                Marshal.FreeHGlobal(preparsed);
            }
        }

        private void HandleBrightnessUsage(IntPtr device, ushort usage, HashSet<ushort> current, HashSet<ushort> previous, int offset)
        {
            if (!current.Contains(usage))
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            DateTime last;
            bool repeatedAfterDelay = lastTrigger.TryGetValue(usage, out last) && (now - last).TotalMilliseconds >= 110;
            if (!previous.Contains(usage) || repeatedAfterDelay)
            {
                lastTrigger[usage] = now;
                commandQueue.Add(offset);
                Logger.Write((offset > 0 ? "Brightness up" : "Brightness down")
                    + " received from raw-input device 0x" + device.ToInt64().ToString("X", CultureInfo.InvariantCulture) + ".");
            }
        }

        private void ProcessCommands()
        {
            foreach (int offset in commandQueue.GetConsumingEnumerable())
            {
                if (shuttingDown)
                {
                    return;
                }

                try
                {
                    int combinedOffset = offset;
                    int queuedOffset;
                    while (commandQueue.TryTake(out queuedOffset))
                    {
                        combinedOffset += queuedOffset;
                    }

                    string result = MonitorController.AdjustAll(combinedOffset);
                    Logger.Write("Direct DDC/CI adjustment: " + result);
                }
                catch (Exception ex)
                {
                    Logger.Write("Direct DDC/CI command failed: " + ex.Message);
                }
            }
        }

        private static string FormatUsages(ushort[] usages, uint count)
        {
            StringBuilder value = new StringBuilder();
            uint limit = Math.Min(count, (uint)usages.Length);
            for (uint index = 0; index < limit; index++)
            {
                if (index > 0)
                {
                    value.Append(',');
                }
                value.Append("0x");
                value.Append(usages[index].ToString("X4", CultureInfo.InvariantCulture));
            }
            return value.Length == 0 ? "none" : value.ToString();
        }

        private static string FormatBytes(IntPtr report, uint reportSize)
        {
            int count = (int)Math.Min(reportSize, 32);
            byte[] bytes = new byte[count];
            Marshal.Copy(report, bytes, 0, count);
            return BitConverter.ToString(bytes);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !shuttingDown)
            {
                shuttingDown = true;
                commandQueue.CompleteAdding();
                if (commandThread != null && commandThread.IsAlive)
                {
                    commandThread.Join(500);
                }
                commandQueue.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class MonitorController
    {
        private const byte BrightnessVcpCode = 0x10;

        public static string AdjustAll(int offsetPercent)
        {
            List<string> successes = new List<string>();
            List<string> failures = new List<string>();
            int physicalMonitorCount = 0;

            NativeMethods.MonitorEnumProc callback = delegate(IntPtr logicalMonitor, IntPtr deviceContext, ref NativeRect monitorRect, IntPtr data)
            {
                uint count;
                if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(logicalMonitor, out count) || count == 0)
                {
                    failures.Add("logical monitor enumeration failed (Win32 "
                        + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ")");
                    return true;
                }

                PhysicalMonitor[] physicalMonitors = new PhysicalMonitor[count];
                if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(logicalMonitor, count, physicalMonitors))
                {
                    failures.Add("physical monitor enumeration failed (Win32 "
                        + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ")");
                    return true;
                }

                physicalMonitorCount += physicalMonitors.Length;
                try
                {
                    foreach (PhysicalMonitor monitor in physicalMonitors)
                    {
                        try
                        {
                            successes.Add(AdjustPhysicalMonitor(monitor, offsetPercent));
                        }
                        catch (Exception ex)
                        {
                            string name = String.IsNullOrWhiteSpace(monitor.Description) ? "unnamed monitor" : monitor.Description.Trim();
                            failures.Add(name + ": " + ex.Message);
                        }
                    }
                }
                finally
                {
                    NativeMethods.DestroyPhysicalMonitors(count, physicalMonitors);
                }

                return true;
            };

            if (!NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "EnumDisplayMonitors failed.");
            }

            GC.KeepAlive(callback);

            if (successes.Count == 0)
            {
                string details = failures.Count == 0 ? "no physical monitors were returned" : String.Join("; ", failures.ToArray());
                throw new InvalidOperationException("No controllable DDC/CI display was found ("
                    + physicalMonitorCount.ToString(CultureInfo.InvariantCulture) + " physical): " + details);
            }

            string summary = String.Join("; ", successes.ToArray());
            if (failures.Count > 0)
            {
                summary += " | skipped: " + String.Join("; ", failures.ToArray());
            }
            return summary;
        }

        private static string AdjustPhysicalMonitor(PhysicalMonitor monitor, int offsetPercent)
        {
            uint minimum;
            uint current;
            uint maximum;
            bool highLevel = NativeMethods.GetMonitorBrightness(monitor.Handle, out minimum, out current, out maximum);

            if (!highLevel)
            {
                minimum = 0;
                if (!NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                    monitor.Handle, BrightnessVcpCode, IntPtr.Zero, out current, out maximum))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new System.ComponentModel.Win32Exception(error, "Brightness query failed");
                }
            }

            if (maximum <= minimum)
            {
                throw new InvalidOperationException("The monitor returned an invalid brightness range.");
            }

            int beforePercent = (int)Math.Round(
                ((double)(current - minimum) * 100.0) / (double)(maximum - minimum),
                MidpointRounding.AwayFromZero);
            int afterPercent = Math.Max(0, Math.Min(100, beforePercent + offsetPercent));
            uint target = minimum + (uint)Math.Round(
                ((double)(maximum - minimum) * afterPercent) / 100.0,
                MidpointRounding.AwayFromZero);

            if (offsetPercent != 0 && target != current)
            {
                bool set = highLevel
                    ? NativeMethods.SetMonitorBrightness(monitor.Handle, target)
                    : NativeMethods.SetVCPFeature(monitor.Handle, BrightnessVcpCode, target);

                if (!set && highLevel)
                {
                    set = NativeMethods.SetVCPFeature(monitor.Handle, BrightnessVcpCode, target);
                }

                if (!set)
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new System.ComponentModel.Win32Exception(error, "Brightness update failed");
                }
            }

            string name = String.IsNullOrWhiteSpace(monitor.Description) ? "monitor" : monitor.Description.Trim();
            string method = highLevel ? "high-level DDC/CI" : "VCP 0x10";
            return name + " " + beforePercent.ToString(CultureInfo.InvariantCulture) + "%→"
                + afterPercent.ToString(CultureInfo.InvariantCulture) + "% via " + method;
        }
    }

    internal static class Logger
    {
        private static readonly object Gate = new object();
        private static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrightnessKeyBridge");
        private static readonly string LogPath = Path.Combine(DirectoryPath, "bridge.log");

        public static void Write(string message)
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(DirectoryPath);
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 262144)
                    {
                        File.Delete(LogPath);
                    }
                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                        + " " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
                catch
                {
                }
            }
        }
    }

    internal enum HidpReportType
    {
        Input = 0,
        Output = 1,
        Feature = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PhysicalMonitor
    {
        public IntPtr Handle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    internal static class NativeMethods
    {
        internal delegate bool MonitorEnumProc(
            IntPtr monitor,
            IntPtr deviceContext,
            ref NativeRect monitorRect,
            IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterRawInputDevices(
            [In] RawInputDevice[] devices,
            uint numberOfDevices,
            uint size);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetRawInputData(
            IntPtr rawInput,
            uint command,
            IntPtr data,
            ref uint size,
            uint headerSize);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetRawInputDeviceInfo(
            IntPtr device,
            uint command,
            IntPtr data,
            ref uint size);

        [DllImport("hid.dll")]
        internal static extern uint HidP_MaxUsageListLength(
            HidpReportType reportType,
            ushort usagePage,
            IntPtr preparsedData);

        [DllImport("hid.dll")]
        internal static extern int HidP_GetUsages(
            HidpReportType reportType,
            ushort usagePage,
            ushort linkCollection,
            [Out] ushort[] usageList,
            ref uint usageLength,
            IntPtr preparsedData,
            IntPtr report,
            uint reportLength);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(
            IntPtr deviceContext,
            IntPtr clipRect,
            MonitorEnumProc callback,
            IntPtr data);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
            IntPtr monitor,
            out uint numberOfPhysicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetPhysicalMonitorsFromHMONITOR(
            IntPtr monitor,
            uint physicalMonitorArraySize,
            [Out] PhysicalMonitor[] physicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyPhysicalMonitors(
            uint physicalMonitorArraySize,
            [In] PhysicalMonitor[] physicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorBrightness(
            IntPtr physicalMonitor,
            out uint minimumBrightness,
            out uint currentBrightness,
            out uint maximumBrightness);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetMonitorBrightness(
            IntPtr physicalMonitor,
            uint newBrightness);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVCPFeatureAndVCPFeatureReply(
            IntPtr physicalMonitor,
            byte vcpCode,
            IntPtr vcpCodeType,
            out uint currentValue,
            out uint maximumValue);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetVCPFeature(
            IntPtr physicalMonitor,
            byte vcpCode,
            uint newValue);
    }
}
