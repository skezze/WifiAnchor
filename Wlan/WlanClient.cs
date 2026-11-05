using System.Runtime.InteropServices;
using System.Text;
using WifiBssidTool.Infrastructure;

namespace WifiBssidTool.Wlan;

internal sealed record WlanInterface(Guid Guid, string Description, int State);

internal sealed record AccessPoint(
    string Ssid, string Bssid, int Signal, int Rssi, string Channel);

internal sealed record ConnectionInfo(
    string Ssid, string Bssid, string Profile, int Signal);

/// <summary>
/// Тонкая обёртка над Windows Native Wifi API (wlanapi.dll).
/// Всё, что раньше делалось через разбор вывода netsh, читается напрямую
/// из API, поэтому не зависит от языка системы.
/// </summary>
internal sealed class WlanClient : IDisposable
{
    private const int InterfaceStateConnected = 1;
    private const int OpcodeCurrentConnection = 7;
    private const int BssTypeInfrastructure = 1;
    private const int BssTypeAny = 3;

    private const int BssEntrySize = 360;
    private const int BssListHeaderSize = 8;

    private IntPtr handle;

    private WlanClient(IntPtr handle) => this.handle = handle;

    public static WlanClient Open()
    {
        uint result = WlanOpenHandle(
            2, IntPtr.Zero, out _, out IntPtr client);

        if (result != 0)
        {
            throw new InvalidOperationException(
                $"Не удалось открыть Windows Wi-Fi API: {Describe(result)}");
        }

        return new WlanClient(client);
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero)
        {
            WlanCloseHandle(handle, IntPtr.Zero);
            handle = IntPtr.Zero;
        }
    }

    public static string Describe(uint code) =>
        $"{new System.ComponentModel.Win32Exception((int)code).Message} ({code})";

    // ---------- Интерфейсы ----------

    public List<WlanInterface> EnumInterfaces()
    {
        var result = new List<WlanInterface>();

        if (WlanEnumInterfaces(handle, IntPtr.Zero, out IntPtr list) != 0)
            return result;

        try
        {
            int count = Marshal.ReadInt32(list);
            int size = Marshal.SizeOf<WLAN_INTERFACE_INFO>();

            for (int i = 0; i < count; i++)
            {
                IntPtr entry = IntPtr.Add(list, 8 + i * size);
                var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(entry);
                result.Add(new WlanInterface(
                    info.InterfaceGuid, info.Description, info.State));
            }
        }
        finally
        {
            WlanFreeMemory(list);
        }

        return result;
    }

    /// <summary>
    /// Выбирает рабочий Wi-Fi адаптер: пропускает виртуальные
    /// (Wi-Fi Direct и т.п.), предпочитает подключённый.
    /// </summary>
    public WlanInterface? GetInterface()
    {
        List<WlanInterface> all = EnumInterfaces();

        List<WlanInterface> real = all
            .Where(i =>
                !i.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                !i.Description.Contains("Direct", StringComparison.OrdinalIgnoreCase))
            .ToList();

        List<WlanInterface> pool = real.Count > 0 ? real : all;

        return pool.FirstOrDefault(i => i.State == InterfaceStateConnected)
            ?? pool.FirstOrDefault();
    }

    // ---------- Сканирование ----------

    public uint RequestScan()
    {
        WlanInterface? itf = GetInterface();
        if (itf == null)
            return 1168; // ERROR_NOT_FOUND

        Guid guid = itf.Guid;
        return WlanScan(
            handle, ref guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Список BSS из кэша Windows (обновляется после RequestScan).</summary>
    public List<AccessPoint> GetAccessPoints()
    {
        var result = new List<AccessPoint>();

        WlanInterface? itf = GetInterface();
        if (itf == null)
            return result;

        Guid guid = itf.Guid;

        uint r = WlanGetNetworkBssList(
            handle, ref guid, IntPtr.Zero, BssTypeAny,
            false, IntPtr.Zero, out IntPtr list);

        if (r != 0)
            return result;

        try
        {
            int count = Marshal.ReadInt32(list, 4);

            for (int i = 0; i < count; i++)
            {
                IntPtr entry = IntPtr.Add(
                    list, BssListHeaderSize + i * BssEntrySize);

                // WLAN_BSS_ENTRY: ssid(0) phyId(36) bssid(40) rssi(56)
                // linkQuality(60) centerFreq kHz(92)
                int ssidLen = Math.Min(Marshal.ReadInt32(entry, 0), 32);
                var ssidBytes = new byte[ssidLen];
                Marshal.Copy(IntPtr.Add(entry, 4), ssidBytes, 0, ssidLen);
                string ssid = Encoding.UTF8.GetString(ssidBytes);

                var mac = new byte[6];
                Marshal.Copy(IntPtr.Add(entry, 40), mac, 0, 6);

                int rssi = Marshal.ReadInt32(entry, 56);
                int quality = Marshal.ReadInt32(entry, 60);
                uint freqKhz = (uint)Marshal.ReadInt32(entry, 92);

                result.Add(new AccessPoint(
                    ssid, Mac.Format(mac), quality, rssi,
                    ChannelFromFrequency(freqKhz)));
            }
        }
        finally
        {
            WlanFreeMemory(list);
        }

        return result;
    }

    private static string ChannelFromFrequency(uint khz)
    {
        uint mhz = khz / 1000;

        if (mhz == 2484) return "14 (2.4 ГГц)";
        if (mhz is >= 2412 and <= 2472) return $"{(mhz - 2407) / 5} (2.4 ГГц)";
        if (mhz is >= 5160 and <= 5885) return $"{(mhz - 5000) / 5} (5 ГГц)";
        if (mhz is >= 5955 and <= 7115) return $"{(mhz - 5950) / 5} (6 ГГц)";

        return mhz > 0 ? $"{mhz} МГц" : "?";
    }

    // ---------- Текущее подключение ----------

    /// <summary>Текущее соединение или null, если адаптер не подключён.</summary>
    public ConnectionInfo? GetCurrentConnection()
    {
        WlanInterface? itf = GetInterface();
        if (itf == null)
            return null;

        Guid guid = itf.Guid;

        uint r = WlanQueryInterface(
            handle, ref guid, OpcodeCurrentConnection, IntPtr.Zero,
            out uint size, out IntPtr data, out _);

        if (r != 0)
            return null;

        try
        {
            // WLAN_CONNECTION_ATTRIBUTES: state(0) mode(4) profile(8, 256 WCHAR)
            // ssid(520) bssType(556) bssid(560) ... signalQuality(576)
            if (size < 580 || Marshal.ReadInt32(data, 0) != InterfaceStateConnected)
                return null;

            string profile = Marshal.PtrToStringUni(IntPtr.Add(data, 8)) ?? "";

            int ssidLen = Math.Min(Marshal.ReadInt32(data, 520), 32);
            var ssidBytes = new byte[ssidLen];
            Marshal.Copy(IntPtr.Add(data, 524), ssidBytes, 0, ssidLen);

            var mac = new byte[6];
            Marshal.Copy(IntPtr.Add(data, 560), mac, 0, 6);

            int quality = Marshal.ReadInt32(data, 576);

            return new ConnectionInfo(
                Encoding.UTF8.GetString(ssidBytes),
                Mac.Format(mac), profile, quality);
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    // ---------- Профили ----------

    public List<string> GetProfileNames()
    {
        var result = new List<string>();

        WlanInterface? itf = GetInterface();
        if (itf == null)
            return result;

        Guid guid = itf.Guid;

        if (WlanGetProfileList(handle, ref guid, IntPtr.Zero, out IntPtr list) != 0)
            return result;

        try
        {
            int count = Marshal.ReadInt32(list);
            const int entrySize = 256 * 2 + 4; // имя (WCHAR[256]) + флаги

            for (int i = 0; i < count; i++)
            {
                string? name = Marshal.PtrToStringUni(
                    IntPtr.Add(list, 8 + i * entrySize));

                if (!string.IsNullOrEmpty(name))
                    result.Add(name);
            }
        }
        finally
        {
            WlanFreeMemory(list);
        }

        return result;
    }

    // ---------- Подключение ----------

    public uint Disconnect()
    {
        WlanInterface? itf = GetInterface();
        if (itf == null)
            return 1168;

        Guid guid = itf.Guid;
        return WlanDisconnect(handle, ref guid, IntPtr.Zero);
    }

    /// <summary>
    /// Просит Windows подключиться к профилю, разрешив только указанный BSSID.
    /// Код 0 означает лишь то, что запрос принят, а не что подключение удалось.
    /// </summary>
    public uint ConnectToBssid(string profileName, string bssid)
    {
        WlanInterface? itf = GetInterface();
        if (itf == null)
            return 1168;

        byte[] mac = Mac.Parse(bssid);
        if (mac.Length != 6)
            return 87;

        Guid guid = itf.Guid;

        // DOT11_BSSID_LIST с одним адресом:
        // NDIS_OBJECT_HEADER { Type=0x80, Revision=1, Size } +
        // uNumOfEntries + uTotalNumOfEntries + BSSIDs[1]
        const int listSize = 20;
        IntPtr bssidList = Marshal.AllocHGlobal(listSize);
        IntPtr profilePtr = IntPtr.Zero;

        try
        {
            for (int i = 0; i < listSize; i++)
                Marshal.WriteByte(bssidList, i, 0);

            Marshal.WriteByte(bssidList, 0, 0x80);
            Marshal.WriteByte(bssidList, 1, 1);
            Marshal.WriteInt16(bssidList, 2, listSize);
            Marshal.WriteInt32(bssidList, 4, 1);
            Marshal.WriteInt32(bssidList, 8, 1);
            Marshal.Copy(mac, 0, IntPtr.Add(bssidList, 12), 6);

            profilePtr = Marshal.StringToHGlobalUni(profileName);

            var parameters = new WLAN_CONNECTION_PARAMETERS
            {
                ConnectionMode = 0, // wlan_connection_mode_profile
                Profile = profilePtr,
                Dot11Ssid = IntPtr.Zero,
                DesiredBssidList = bssidList,
                BssType = BssTypeInfrastructure,
                Flags = 0
            };

            return WlanConnect(handle, ref guid, ref parameters, IntPtr.Zero);
        }
        finally
        {
            if (profilePtr != IntPtr.Zero)
                Marshal.FreeHGlobal(profilePtr);

            Marshal.FreeHGlobal(bssidList);
        }
    }

    // ---------- P/Invoke ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;

        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_PARAMETERS
    {
        public int ConnectionMode;
        public IntPtr Profile;
        public IntPtr Dot11Ssid;
        public IntPtr DesiredBssidList;
        public int BssType;
        public uint Flags;
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(
        uint clientVersion, IntPtr reserved,
        out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(
        IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(
        IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanScan(
        IntPtr clientHandle, ref Guid interfaceGuid,
        IntPtr dot11Ssid, IntPtr ieData, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetNetworkBssList(
        IntPtr clientHandle, ref Guid interfaceGuid,
        IntPtr dot11Ssid, int dot11BssType,
        [MarshalAs(UnmanagedType.Bool)] bool securityEnabled,
        IntPtr reserved, out IntPtr wlanBssList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(
        IntPtr clientHandle, ref Guid interfaceGuid, int opCode,
        IntPtr reserved, out uint dataSize, out IntPtr data,
        out int opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetProfileList(
        IntPtr clientHandle, ref Guid interfaceGuid,
        IntPtr reserved, out IntPtr profileList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanDisconnect(
        IntPtr clientHandle, ref Guid interfaceGuid, IntPtr reserved);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanConnect(
        IntPtr clientHandle, ref Guid interfaceGuid,
        ref WLAN_CONNECTION_PARAMETERS connectionParameters,
        IntPtr reserved);
}
