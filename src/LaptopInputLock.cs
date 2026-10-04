// Laptop Input Lock - temporarily disables a laptop's built-in keyboard and touchpad.
//
//   LaptopInputLock.exe          : lock (run again while locked to unlock)
//   LaptopInputLock.exe /detect  : detect the built-in devices and save them to devices.txt
//
// devices.txt lists the device instance IDs to lock, one per line (wildcards * and ? allowed,
// text after # is a comment).
//
// Windows does not allow keyboard / touchpad class devices to be disabled, so this tool removes
// the device nodes (pnputil /remove-device) and brings them back with a hardware rescan
// (pnputil /scan-devices) when unlocking. A reboot always restores every device.
//
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Laptop Input Lock")]
[assembly: AssemblyDescription("Temporarily disables a laptop's built-in keyboard and touchpad.")]
[assembly: AssemblyProduct("Laptop Input Lock")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Ishikawa Tatsuya. MIT License.")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

static class Program
{
    public const string AppName = "Laptop Input Lock";

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();

        if (Array.Exists(args, a => a.Equals("/detect", StringComparison.OrdinalIgnoreCase)))
        {
            Application.Run(new DetectForm());
            return;
        }

        bool created;
        using (var mutex = new Mutex(true, @"Local\LaptopInputLock", out created))
        {
            if (!created)
            {
                // Already locked: ask the running instance to unlock.
                EventWaitHandle ev;
                if (EventWaitHandle.TryOpenExisting(@"Local\LaptopInputLock_Unlock", out ev)) ev.Set();
                return;
            }

            var patterns = Config.Load();
            if (patterns.Count == 0)
            {
                if (MessageBox.Show("No devices are configured yet.\nOpen the device detection window?",
                        AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    Application.Run(new DetectForm());
                return;
            }
            if (!LockContext.ConfirmExternalMouse(patterns)) return;
            Application.Run(new LockContext(patterns));
        }
    }
}

// Reads and writes devices.txt.
static class Config
{
    public static readonly string Path = System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(Application.ExecutablePath), "devices.txt");

    public static List<string> Load()
    {
        var list = new List<string>();
        if (!File.Exists(Path)) return list;
        foreach (var raw in File.ReadAllLines(Path, Encoding.UTF8))
        {
            string line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line.Substring(0, hash);
            line = line.Trim();
            if (line.Length > 0) list.Add(line);
        }
        return list;
    }

    public static void Save(List<string> lines)
    {
        File.WriteAllLines(Path, lines.ToArray(), new UTF8Encoding(true));
    }

    public static Regex ToRegex(string pattern)
    {
        string re = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
        return new Regex("^" + re + "$", RegexOptions.IgnoreCase);
    }

    // Pattern with the last part of the instance ID (the instance number) replaced by a wildcard.
    public static string PatternFor(string instanceId)
    {
        int i = instanceId.LastIndexOf('\\');
        return i < 0 ? instanceId : instanceId.Substring(0, i + 1) + "*";
    }
}

class LockContext : ApplicationContext
{
    readonly List<Regex> patterns = new List<Regex>();
    readonly List<string> enumerators = new List<string>(); // null = all enumerators
    readonly NotifyIcon tray;
    readonly Control invoker = new Control();
    readonly EventWaitHandle unlockEvent;
    readonly AutoResetEvent recheck = new AutoResetEvent(false);
    readonly object sync = new object();
    readonly List<string> removedIds = new List<string>();
    readonly DeviceChangeWatcher watcher;
    bool unlocking;
    bool locked = true;

    public LockContext(List<string> patternTexts)
    {
        foreach (var p in patternTexts)
        {
            patterns.Add(Config.ToRegex(p));
            // Only enumerating the pattern's enumerator (HID, ACPI, ...) keeps checks fast.
            string e = p.Split('\\')[0];
            if (e.IndexOfAny(new[] { '*', '?' }) >= 0) e = null;
            if (!enumerators.Contains(e)) enumerators.Add(e);
        }
        if (enumerators.Contains(null)) { enumerators.Clear(); enumerators.Add(null); }

        var h = invoker.Handle; // create the handle used to marshal back to the UI thread

        tray = new NotifyIcon();
        tray.Icon = SystemIcons.Shield;
        tray.Text = "Built-in keyboard and touchpad are locked";
        var menu = new ContextMenuStrip();
        menu.Items.Add("Unlock and exit", null, delegate { Unlock(); });
        tray.ContextMenuStrip = menu;
        tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Unlock(); };
        tray.Visible = true;

        unlockEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LaptopInputLock_Unlock");
        var t = new Thread(delegate() {
            unlockEvent.WaitOne();
            invoker.BeginInvoke((MethodInvoker)Unlock);
        });
        t.IsBackground = true;
        t.Start();

        string error = RemoveDevices();

        // Devices come back when another process triggers a hardware rescan, so while locked
        // re-check on every device change notification and every 2 seconds, and remove them again.
        watcher = new DeviceChangeWatcher(delegate { recheck.Set(); });
        var guard = new Thread(Guard);
        guard.IsBackground = true;
        guard.Start();

        if (error != null)
            tray.ShowBalloonTip(5000, "Locked (with errors)", error, ToolTipIcon.Warning);
        else
            tray.ShowBalloonTip(3000, "Locked", "The built-in keyboard and touchpad are locked.\nRun the tool again or click this icon to unlock.", ToolTipIcon.Info);
    }

    // If no external mouse is found, confirm before locking.
    public static bool ConfirmExternalMouse(List<string> patternTexts)
    {
        var regs = patternTexts.ConvertAll(Config.ToRegex);
        foreach (var id in DeviceEnum.PresentIds(DeviceEnum.MouseClass, null))
        {
            if (id.StartsWith(@"ACPI\", StringComparison.OrdinalIgnoreCase)) continue; // often a phantom PS/2 mouse
            if (!regs.Exists(r => r.IsMatch(id))) return true;
        }
        return MessageBox.Show(
            "No external mouse was found.\n" +
            "Once locked, the built-in keyboard and touchpad stay unusable until\n" +
            "you run this tool again or restart the PC.\n\nLock anyway?",
            Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    void Guard()
    {
        while (true)
        {
            recheck.WaitOne(2000);
            lock (sync)
            {
                if (!locked) return;
                if (FindDevices().Count > 0) RemoveDevices();
            }
        }
    }

    void Unlock()
    {
        if (unlocking) return;
        unlocking = true;

        watcher.DestroyHandle();
        lock (sync) locked = false;
        recheck.Set();

        string error = RestoreDevices();
        if (error != null)
            tray.ShowBalloonTip(5000, "Unlocked (with errors)", error, ToolTipIcon.Warning);
        else
            tray.ShowBalloonTip(2000, "Unlocked", "The built-in keyboard and touchpad are enabled again.", ToolTipIcon.Info);

        var timer = new System.Windows.Forms.Timer();
        timer.Interval = error != null ? 5000 : 2500;
        timer.Tick += delegate { timer.Stop(); tray.Visible = false; tray.Dispose(); ExitThread(); };
        timer.Start();
    }

    List<string> FindDevices()
    {
        var ids = new List<string>();
        foreach (var e in enumerators)
            foreach (var id in DeviceEnum.PresentIds(null, e))
                if (patterns.Exists(r => r.IsMatch(id)) && !ids.Contains(id)) ids.Add(id);
        return ids;
    }

    string RemoveDevices()
    {
        var errors = new List<string>();
        foreach (var id in FindDevices())
        {
            lock (removedIds) if (!removedIds.Contains(id)) removedIds.Add(id);
            int code;
            string output = PnpUtil("/remove-device \"" + id + "\"", out code);
            if (code != 0 && code != 3010) errors.Add(id + ": " + output.Trim());
        }
        if (FindDevices().Count > 0) errors.Add("Some devices could not be removed.");
        return errors.Count > 0 ? string.Join("\n", errors) : null;
    }

    string RestoreDevices()
    {
        int code;
        PnpUtil("/scan-devices", out code);
        for (int i = 0; i < 20; i++)
        {
            var present = FindDevices();
            bool all;
            lock (removedIds) all = removedIds.TrueForAll(id => present.Contains(id));
            if (all) return null;
            Thread.Sleep(300);
        }
        return "Some devices were not detected again. Restart the PC to restore them.";
    }

    static string PnpUtil(string args, out int exitCode)
    {
        var psi = new ProcessStartInfo(
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe"), args);
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        using (var p = Process.Start(psi))
        {
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            exitCode = p.ExitCode;
            return output;
        }
    }
}

// Detection window: uses Raw Input to find which devices input actually comes from,
// and saves the selected ones to devices.txt.
class DetectForm : Form
{
    const int WM_INPUT = 0xFF;
    const string KindKeyboard = "Keyboard", KindTouchpad = "Touchpad",
                 KindTouchpadMouse = "Touchpad (mouse)", KindMouse = "Mouse";

    [StructLayout(LayoutKind.Sequential)] struct RID { public ushort page, usage; public uint flags; public IntPtr hwnd; }
    [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(RID[] d, uint n, uint sz);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint hdr);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr h, uint cmd, StringBuilder data, ref uint size);

    readonly ListView list = new ListView();
    readonly Dictionary<string, ListViewItem> items = new Dictionary<string, ListViewItem>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<IntPtr, string> handleIds = new Dictionary<IntPtr, string>();

    public DetectForm()
    {
        Text = Program.AppName + " - Detect devices";
        ClientSize = new Size(820, 420);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        var info = new Label();
        info.Dock = DockStyle.Top;
        info.Height = 96;
        info.Padding = new Padding(10, 8, 10, 0);
        info.Text =
            "1. Type a few characters on the built-in keyboard.\n" +
            "2. Move your finger on the touchpad.\n" +
            "Detected devices appear below. Check the ones to lock and click Save.\n" +
            "Note: external keyboards and mice you use will also appear. Uncheck them.";

        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.CheckBoxes = true;
        list.FullRowSelect = true;
        list.Columns.Add("Type", 150);
        list.Columns.Add("Name", 210);
        list.Columns.Add("Inputs", 60);
        list.Columns.Add("Device instance ID", 380);

        var buttons = new FlowLayoutPanel();
        buttons.Dock = DockStyle.Bottom;
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Height = 44;
        buttons.Padding = new Padding(6);
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        cancel.Click += delegate { Close(); };
        var save = new Button { Text = "Save", AutoSize = true };
        save.Click += delegate { Save(); };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);

        Controls.Add(list);
        Controls.Add(info);
        Controls.Add(buttons);
        list.BringToFront();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        const uint INPUTSINK = 0x100;
        var rid = new RID[] {
            new RID { page = 0x01, usage = 0x06, flags = INPUTSINK, hwnd = Handle }, // keyboard
            new RID { page = 0x01, usage = 0x02, flags = INPUTSINK, hwnd = Handle }, // mouse (incl. non-precision touchpads)
            new RID { page = 0x0D, usage = 0x05, flags = INPUTSINK, hwnd = Handle }, // precision touchpad
        };
        RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf(typeof(RID)));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT) ReadRaw(m.LParam);
        base.WndProc(ref m);
    }

    void ReadRaw(IntPtr hRaw)
    {
        uint hs = (uint)(8 + IntPtr.Size * 2), size = 0;
        GetRawInputData(hRaw, 0x10000003, IntPtr.Zero, ref size, hs);
        if (size == 0) return;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRaw, 0x10000003, buf, ref size, hs) != size) return;
            int type = Marshal.ReadInt32(buf, 0);
            IntPtr dev = Marshal.ReadIntPtr(buf, 8);
            if (dev == IntPtr.Zero) return; // source device unknown (e.g. mouse input synthesized from a precision touchpad)
            string kind = type == 1 ? KindKeyboard : type == 2 ? KindTouchpad : KindMouse;
            string id = InstanceIdOf(dev);
            if (id == null) return;
            AddOrCount(id, kind, true);
            if (kind == KindTouchpad) AddTouchpadSiblings(id);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    string InstanceIdOf(IntPtr dev)
    {
        string id;
        if (handleIds.TryGetValue(dev, out id)) return id;
        uint n = 0;
        GetRawInputDeviceInfo(dev, 0x20000007, null, ref n);
        if (n == 0) return null;
        var sb = new StringBuilder((int)n + 1);
        GetRawInputDeviceInfo(dev, 0x20000007, sb, ref n);
        // \\?\HID#VID_xxxx&PID_xxxx&Col03#6&2eacc9f4&0&0002#{GUID} -> HID\VID_xxxx&PID_xxxx&COL03\6&2EACC9F4&0&0002
        string s = sb.ToString();
        if (s.StartsWith(@"\\?\")) s = s.Substring(4);
        int g = s.LastIndexOf("#{");
        if (g >= 0) s = s.Substring(0, g);
        id = s.Replace('#', '\\').ToUpperInvariant();
        handleIds[dev] = id;
        return id;
    }

    // Mouse devices sharing the precision touchpad's parent (they move the cursor) are locked too.
    void AddTouchpadSiblings(string touchpadId)
    {
        string parent = DevNode.Parent(touchpadId);
        if (parent == null) return;
        foreach (var id in DeviceEnum.PresentIds(DeviceEnum.MouseClass, null))
            if (string.Equals(DevNode.Parent(id), parent, StringComparison.OrdinalIgnoreCase))
                AddOrCount(id, KindTouchpadMouse, false);
    }

    void AddOrCount(string id, string kind, bool counted)
    {
        ListViewItem item;
        if (!items.TryGetValue(id, out item))
        {
            item = new ListViewItem(kind);
            item.SubItems.Add(DevNode.Name(id) ?? "");
            item.SubItems.Add(counted ? "0" : "-");
            item.SubItems.Add(id);
            item.Checked = kind != KindMouse; // plain mice are probably external, so unchecked by default
            items[id] = item;
            list.Items.Add(item);
        }
        if (counted)
        {
            int c;
            int.TryParse(item.SubItems[2].Text, out c);
            item.SubItems[2].Text = (c + 1).ToString();
        }
    }

    void Save()
    {
        var lines = new List<string> {
            "# Devices locked by Laptop Input Lock (instance IDs, wildcards * and ? allowed)",
            "# Recreate this file with: LaptopInputLock.exe /detect",
        };
        bool hasAcpi = false;
        foreach (ListViewItem item in list.Items)
        {
            if (!item.Checked) continue;
            string id = item.SubItems[3].Text;
            lines.Add("");
            lines.Add("# " + item.Text + ": " + item.SubItems[1].Text);
            lines.Add(Config.PatternFor(id));
            if (id.StartsWith(@"ACPI\", StringComparison.OrdinalIgnoreCase)) hasAcpi = true;
        }
        if (lines.Count == 2)
        {
            MessageBox.Show("No devices are checked.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (hasAcpi && MessageBox.Show(
                "The selection includes a PS/2 (ACPI\\) device.\n" +
                "Such devices may not come back with a rescan, so unlocking may require a restart.\n\nSave anyway?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        Config.Save(lines);
        MessageBox.Show("Saved.\n" + Config.Path, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        Close();
    }
}

// Enumerates present device instance IDs with SetupAPI (orders of magnitude faster than running pnputil).
static class DeviceEnum
{
    public static readonly Guid MouseClass = new Guid("4d36e96f-e325-11ce-bfc1-08002be10318");
    const int DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    public static List<string> PresentIds(Guid? classGuid, string enumerator)
    {
        var ids = new List<string>();
        IntPtr guidPtr = IntPtr.Zero;
        int flags = DIGCF_PRESENT;
        if (classGuid.HasValue)
        {
            guidPtr = Marshal.AllocHGlobal(16);
            Marshal.StructureToPtr(classGuid.Value, guidPtr, false);
        }
        else flags |= DIGCF_ALLCLASSES;
        try
        {
            IntPtr set = SetupDiGetClassDevs(guidPtr, enumerator, IntPtr.Zero, flags);
            if (set == new IntPtr(-1)) return ids;
            try
            {
                var data = new SP_DEVINFO_DATA();
                data.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                var sb = new StringBuilder(512);
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    int req;
                    if (SetupDiGetDeviceInstanceId(set, ref data, sb, sb.Capacity, out req)) ids.Add(sb.ToString());
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
        finally { if (guidPtr != IntPtr.Zero) Marshal.FreeHGlobal(guidPtr); }
        return ids;
    }
}

// Looks up a device's parent and display name with the Configuration Manager API.
static class DevNode
{
    [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[] buffer, ref uint size, uint flags);

    public static string Parent(string id)
    {
        uint inst, parent;
        if (CM_Locate_DevNodeW(out inst, id, 0) != 0) return null;
        if (CM_Get_Parent(out parent, inst, 0) != 0) return null;
        var sb = new StringBuilder(512);
        if (CM_Get_Device_IDW(parent, sb, sb.Capacity, 0) != 0) return null;
        return sb.ToString();
    }

    // Returns the FriendlyName if present, otherwise the DeviceDesc.
    public static string Name(string id)
    {
        uint inst;
        if (CM_Locate_DevNodeW(out inst, id, 0) != 0) return null;
        return StringProperty(inst, 14) ?? StringProperty(inst, 2);
    }

    static string StringProperty(uint inst, uint pid)
    {
        var key = new DEVPROPKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = pid }; // DEVPKEY_Device_*
        var buf = new byte[1024];
        uint size = (uint)buf.Length, type;
        if (CM_Get_DevNode_PropertyW(inst, ref key, out type, buf, ref size, 0) != 0) return null;
        return Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
    }
}

// Hidden window that receives device change notifications (WM_DEVICECHANGE / DBT_DEVNODES_CHANGED).
class DeviceChangeWatcher : NativeWindow
{
    const int WM_DEVICECHANGE = 0x219, DBT_DEVNODES_CHANGED = 0x7;
    readonly MethodInvoker onChange;

    public DeviceChangeWatcher(MethodInvoker onChange)
    {
        this.onChange = onChange;
        CreateHandle(new CreateParams());
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DEVICECHANGE && m.WParam.ToInt32() == DBT_DEVNODES_CHANGED) onChange();
        base.WndProc(ref m);
    }
}
