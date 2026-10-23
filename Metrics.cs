using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace TaskbarMetrics
{
    sealed class MetricsSnapshot
    {
        public double Cpu = -1;
        public double Gpu = -1;
        public double Memory = -1;
        public double Disk = -1;
        public double NetDown;
        public double NetUp;
        public double DiskRead;
        public double DiskWrite;
        public ulong TotalMemory;
        public ulong AvailableMemory;
        public string NetworkName = "";
        public double UptimeSeconds;
        public DateTime Timestamp;
    }

    sealed class PdhQuery : IDisposable
    {
        const uint PDH_FMT_DOUBLE = 0x00000200;
        const uint PDH_MORE_DATA = 0x800007D2;
        const uint PDH_STATUS_VALID_DATA = 0x00000000;
        const uint PDH_STATUS_NEW_DATA = 0x00000001;

        [StructLayout(LayoutKind.Explicit)]
        struct FmtValue
        {
            [FieldOffset(0)] public uint CStatus;
            [FieldOffset(8)] public double DoubleValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct FmtItem
        {
            public IntPtr Name;
            public FmtValue Value;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern uint PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern uint PdhAddCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format,
            ref uint bufferSize, out uint itemCount, IntPtr buffer);

        [DllImport("pdh.dll")]
        static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format,
            out uint type, out FmtValue value);

        [DllImport("pdh.dll")]
        static extern uint PdhCloseQuery(IntPtr query);

        IntPtr _query;
        IntPtr _gpuCounter;
        IntPtr _diskIdleCounter;
        IntPtr _diskReadCounter;
        IntPtr _diskWriteCounter;

        public readonly bool GpuAvailable;
        public readonly bool DiskAvailable;

        public PdhQuery()
        {
            uint status = PdhOpenQuery(null, IntPtr.Zero, out _query);
            if (status != 0 || _query == IntPtr.Zero)
                throw new InvalidOperationException("PDH 初始化失败 (0x" + status.ToString("X8") + ")");

            _gpuCounter = AddCounterToQuery(@"\GPU Engine(*)\Utilization Percentage");
            GpuAvailable = _gpuCounter != IntPtr.Zero;

            _diskIdleCounter = AddCounterToQuery(@"\PhysicalDisk(_Total)\% Idle Time");
            _diskReadCounter = AddCounterToQuery(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
            _diskWriteCounter = AddCounterToQuery(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
            DiskAvailable = _diskIdleCounter != IntPtr.Zero;

            PdhCollectQueryData(_query);
        }

        IntPtr AddCounterToQuery(string path)
        {
            IntPtr counter;
            uint status = PdhAddEnglishCounter(_query, path, IntPtr.Zero, out counter);
            if (status == 0 && counter != IntPtr.Zero) return counter;
            status = PdhAddCounter(_query, path, IntPtr.Zero, out counter);
            if (status == 0 && counter != IntPtr.Zero) return counter;
            return IntPtr.Zero;
        }

        public bool Collect()
        {
            if (_query == IntPtr.Zero) return false;
            return PdhCollectQueryData(_query) == 0;
        }

        public void ReadGpu(List<string> names, List<double> values)
        {
            names.Clear();
            values.Clear();
            if (_gpuCounter == IntPtr.Zero) return;

            uint size = 0;
            uint count = 0;
            uint status = PdhGetFormattedCounterArray(_gpuCounter, PDH_FMT_DOUBLE,
                ref size, out count, IntPtr.Zero);
            if (status != PDH_MORE_DATA || size == 0) return;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    status = PdhGetFormattedCounterArray(_gpuCounter, PDH_FMT_DOUBLE,
                        ref size, out count, buffer);
                    if (status == PDH_MORE_DATA) continue;
                    if (status != 0) return;

                    int itemSize = Marshal.SizeOf(typeof(FmtItem));
                    for (int i = 0; i < count; i++)
                    {
                        FmtItem item = (FmtItem)Marshal.PtrToStructure(
                            new IntPtr(buffer.ToInt64() + (long)i * itemSize), typeof(FmtItem));
                        if (item.Value.CStatus != PDH_STATUS_VALID_DATA &&
                            item.Value.CStatus != PDH_STATUS_NEW_DATA)
                            continue;
                        string name = Marshal.PtrToStringUni(item.Name);
                        if (string.IsNullOrEmpty(name)) continue;
                        names.Add(name);
                        values.Add(item.Value.DoubleValue);
                    }
                    return;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }

        public bool ReadDiskIdle(out double value) { return ReadValue(_diskIdleCounter, out value); }
        public bool ReadDiskRead(out double value) { return ReadValue(_diskReadCounter, out value); }
        public bool ReadDiskWrite(out double value) { return ReadValue(_diskWriteCounter, out value); }

        static bool ReadValue(IntPtr counter, out double value)
        {
            value = 0;
            if (counter == IntPtr.Zero) return false;
            uint type;
            FmtValue data;
            uint status = PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE, out type, out data);
            if (status != 0) return false;
            if (data.CStatus != PDH_STATUS_VALID_DATA && data.CStatus != PDH_STATUS_NEW_DATA)
                return false;
            if (double.IsNaN(data.DoubleValue) || double.IsInfinity(data.DoubleValue))
                return false;
            value = data.DoubleValue;
            return true;
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
            }
        }
    }

    sealed class MetricsSampler : IDisposable
    {
        readonly object _pdhLock = new object();
        PdhQuery _pdh;
        bool _pdhReadFailed;
        bool _cpuPrimed;
        ulong _prevIdle;
        ulong _prevKernel;
        ulong _prevUser;

        List<NetworkInterface> _networkInterfaces = new List<NetworkInterface>();
        DateTime _networkRefresh;
        string _networkName = "";
        long _prevBytesIn;
        long _prevBytesOut;
        long _prevNetworkTicks;
        bool _networkPrimed;

        public MetricsSampler()
        {
            try
            {
                _pdh = new PdhQuery();
            }
            catch (Exception ex)
            {
                _pdh = null;
                AppLog.Error("PDH unavailable", ex);
            }

            RefreshNetwork();
            PrimeCpu();
            PrimeNetwork();
        }

        public MetricsSnapshot Sample()
        {
            MetricsSnapshot snapshot = new MetricsSnapshot();
            snapshot.Timestamp = DateTime.Now;
            snapshot.UptimeSeconds = Native.GetTickCount64() / 1000.0;

            SampleCpu(snapshot);
            SampleMemory(snapshot);
            SamplePdh(snapshot);
            SampleNetwork(snapshot);
            return snapshot;
        }

        void PrimeCpu()
        {
            Native.FILETIME idle, kernel, user;
            if (Native.GetSystemTimes(out idle, out kernel, out user))
            {
                _prevIdle = Native.ToUInt64(idle);
                _prevKernel = Native.ToUInt64(kernel);
                _prevUser = Native.ToUInt64(user);
                _cpuPrimed = true;
            }
        }

        void SampleCpu(MetricsSnapshot snapshot)
        {
            Native.FILETIME idle, kernel, user;
            if (!Native.GetSystemTimes(out idle, out kernel, out user)) return;

            ulong idleNow = Native.ToUInt64(idle);
            ulong kernelNow = Native.ToUInt64(kernel);
            ulong userNow = Native.ToUInt64(user);

            if (_cpuPrimed)
            {
                ulong idleDelta = idleNow - _prevIdle;
                ulong totalDelta = (kernelNow - _prevKernel) + (userNow - _prevUser);
                if (totalDelta > 0)
                    snapshot.Cpu = Clamp(100.0 * (1.0 - (double)idleDelta / (double)totalDelta), 0, 100);
            }

            _prevIdle = idleNow;
            _prevKernel = kernelNow;
            _prevUser = userNow;
            _cpuPrimed = true;
        }

        void SampleMemory(MetricsSnapshot snapshot)
        {
            Native.MEMORYSTATUSEX mem = new Native.MEMORYSTATUSEX();
            mem.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            if (Native.GlobalMemoryStatusEx(ref mem))
            {
                snapshot.Memory = Clamp(mem.dwMemoryLoad, 0, 100);
                snapshot.TotalMemory = mem.ullTotalPhys;
                snapshot.AvailableMemory = mem.ullAvailPhys;
            }
        }

        void SamplePdh(MetricsSnapshot snapshot)
        {
            lock (_pdhLock)
            {
                if (_pdh == null) return;
                if (!_pdh.Collect())
                {
                    if (!_pdhReadFailed)
                    {
                        _pdhReadFailed = true;
                        AppLog.Info("PDH collect failed; performance counters may be unavailable.");
                    }
                    return;
                }
                _pdhReadFailed = false;

                if (_pdh.GpuAvailable)
                {
                    List<string> names = new List<string>();
                    List<double> values = new List<double>();
                    _pdh.ReadGpu(names, values);
                    double usage = CalculateGpuUsage(names, values);
                    if (usage >= 0) snapshot.Gpu = usage;
                }

                if (_pdh.DiskAvailable)
                {
                    double idle;
                    if (_pdh.ReadDiskIdle(out idle)) snapshot.Disk = Clamp(100.0 - idle, 0, 100);
                    double read;
                    if (_pdh.ReadDiskRead(out read)) snapshot.DiskRead = Math.Max(0, read);
                    double write;
                    if (_pdh.ReadDiskWrite(out write)) snapshot.DiskWrite = Math.Max(0, write);
                }
            }
        }

        static double CalculateGpuUsage(List<string> names, List<double> values)
        {
            if (names.Count == 0) return 0;

            Dictionary<string, double> byEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                if (name.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0) continue;

                int luid = name.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase);
                int engtype = name.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
                if (luid < 0 || engtype <= luid) continue;

                string key = name.Substring(luid + 1, engtype - luid - 1);
                double value = values[i];
                if (value < 0 || double.IsNaN(value) || double.IsInfinity(value)) continue;
                double oldValue;
                byEngine[key] = byEngine.TryGetValue(key, out oldValue) ? oldValue + value : value;
            }

            double maximum = 0;
            foreach (double value in byEngine.Values)
            {
                double capped = Clamp(value, 0, 100);
                if (capped > maximum) maximum = capped;
            }
            return maximum;
        }

        void RefreshNetwork()
        {
            List<NetworkInterface> primary = new List<NetworkInterface>();
            List<NetworkInterface> fallback = new List<NetworkInterface>();
            List<string> names = new List<string>();

            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                            ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                        if (!ni.Supports(NetworkInterfaceComponent.IPv4)) continue;
                        if (ni.Speed <= 0) continue;

                        string description = (ni.Name + " " + ni.Description).ToLowerInvariant();
                        bool virtualAdapter = ContainsAny(description, "virtual", "vpn", "tap-", "tun",
                            "loopback", "bluetooth", "hyper-v", "vmware", "virtualbox", "citrix",
                            "fortinet", "sangfor", "anyconnect", "zerotier", "wireguard", "openvpn", "hamachi");

                        fallback.Add(ni);
                        if (!virtualAdapter) primary.Add(ni);
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Enumerating network interfaces failed", ex);
            }

            string oldName = _networkName;
            bool hadPrevious = _networkPrimed;
            _networkInterfaces = primary.Count > 0 ? primary : fallback;
            foreach (NetworkInterface ni in _networkInterfaces)
            {
                if (names.Count < 3) names.Add(ni.Name);
            }
            if (_networkInterfaces.Count > 3) names.Add("等");
            _networkName = names.Count == 0 ? "无活动网卡" : string.Join(" / ", names.ToArray());
            _networkRefresh = DateTime.UtcNow;
            if (!hadPrevious || !string.Equals(oldName, _networkName, StringComparison.Ordinal))
                PrimeNetwork();
        }

        static bool ContainsAny(string text, params string[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (text.IndexOf(values[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        void PrimeNetwork()
        {
            long bytesIn, bytesOut;
            ReadNetworkBytes(out bytesIn, out bytesOut);
            _prevBytesIn = bytesIn;
            _prevBytesOut = bytesOut;
            _prevNetworkTicks = Stopwatch.GetTimestamp();
            _networkPrimed = true;
        }

        void SampleNetwork(MetricsSnapshot snapshot)
        {
            if ((DateTime.UtcNow - _networkRefresh).TotalSeconds >= 15)
                RefreshNetwork();

            long bytesIn, bytesOut;
            ReadNetworkBytes(out bytesIn, out bytesOut);
            long ticks = Stopwatch.GetTimestamp();

            if (_networkPrimed)
            {
                double seconds = (ticks - _prevNetworkTicks) / (double)Stopwatch.Frequency;
                if (seconds > 0.05)
                {
                    double down = (bytesIn - _prevBytesIn) / seconds;
                    double up = (bytesOut - _prevBytesOut) / seconds;
                    snapshot.NetDown = down >= 0 ? down : 0;
                    snapshot.NetUp = up >= 0 ? up : 0;
                }
            }

            _prevBytesIn = bytesIn;
            _prevBytesOut = bytesOut;
            _prevNetworkTicks = ticks;
            _networkPrimed = true;
            snapshot.NetworkName = _networkName;
        }

        void ReadNetworkBytes(out long bytesIn, out long bytesOut)
        {
            bytesIn = 0;
            bytesOut = 0;
            for (int i = 0; i < _networkInterfaces.Count; i++)
            {
                try
                {
                    IPv4InterfaceStatistics stats = _networkInterfaces[i].GetIPv4Statistics();
                    bytesIn += stats.BytesReceived;
                    bytesOut += stats.BytesSent;
                }
                catch
                {
                }
            }
        }

        internal static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public void Dispose()
        {
            lock (_pdhLock)
            {
                if (_pdh != null)
                {
                    _pdh.Dispose();
                    _pdh = null;
                }
            }
        }
    }
}
