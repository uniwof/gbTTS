using System; using System.Text; using System.Runtime.InteropServices; using System.Diagnostics; using System.Collections.Generic;
public static class W {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr p, uint f);
  public static uint Gdi(IntPtr p) { return GetGuiResources(p,0); }
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] i, int cb);
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte sc, uint f, UIntPtr e);
  [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GTI g);
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] struct GTI { public int cb, flags; public IntPtr act, foc, cap, menu, move, caret; public RECT rc; }
  [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public U u; }
  [StructLayout(LayoutKind.Explicit)] struct U { [FieldOffset(0)] public MI mi; [FieldOffset(0)] public KI ki; }
  [StructLayout(LayoutKind.Sequential)] struct MI { public int dx, dy; public uint data, flags, time; public IntPtr ex; }
  [StructLayout(LayoutKind.Sequential)] struct KI { public ushort vk, sc; public uint flags, time; public IntPtr ex; }
  static INPUT K(ushort vk, bool up) { bool ext = vk==0x23||vk==0x24||vk==0x25||vk==0x26||vk==0x27||vk==0x28; return new INPUT{type=1,u=new U{ki=new KI{vk=vk,flags=(up?2u:0u)|(ext?1u:0u)}}}; }
  public static void Key(ushort vk, bool up) { SendInput(1, new[]{K(vk,up)}, Marshal.SizeOf(typeof(INPUT))); }
  public static void Chord(params ushort[] keys) { var l=new List<INPUT>(); foreach(var k in keys) l.Add(K(k,false)); for(int i=keys.Length-1;i>=0;i--) l.Add(K(keys[i],true)); SendInput((uint)l.Count,l.ToArray(),Marshal.SizeOf(typeof(INPUT))); }
  // 按住修饰键一段时间再松开（模拟真人：WM_HOTKEY 到达时 Ctrl/Alt 仍按着）
  public static void HumanChord(int holdMs, params ushort[] keys) { foreach(var k in keys) { Key(k,false); System.Threading.Thread.Sleep(30);} System.Threading.Thread.Sleep(holdMs); for(int i=keys.Length-1;i>=0;i--){ Key(keys[i],true); System.Threading.Thread.Sleep(20);} }
  public static void Repeat(ushort vk, int n, bool shift) { for(int i=0;i<n;i++){ if(shift) Chord(0x10,vk); else Chord(vk); System.Threading.Thread.Sleep(15);} }
  static INPUT M(uint f) { return new INPUT{type=0,u=new U{mi=new MI{flags=f}}}; }
  public static void Click(int x, int y) { SetCursorPos(x,y); System.Threading.Thread.Sleep(60); SendInput(1,new[]{M(2)},Marshal.SizeOf(typeof(INPUT))); System.Threading.Thread.Sleep(50); SendInput(1,new[]{M(4)},Marshal.SizeOf(typeof(INPUT))); }
  public static void RClick(int x, int y) { SetCursorPos(x,y); System.Threading.Thread.Sleep(60); SendInput(1,new[]{M(8)},Marshal.SizeOf(typeof(INPUT))); System.Threading.Thread.Sleep(50); SendInput(1,new[]{M(16)},Marshal.SizeOf(typeof(INPUT))); }
  public static void DblClick(int x, int y) { Click(x,y); System.Threading.Thread.Sleep(90); SendInput(1,new[]{M(2)},Marshal.SizeOf(typeof(INPUT))); System.Threading.Thread.Sleep(40); SendInput(1,new[]{M(4)},Marshal.SizeOf(typeof(INPUT))); }
  public static void Drag(int x1, int y1, int x2, int y2) { SetCursorPos(x1,y1); System.Threading.Thread.Sleep(60); SendInput(1,new[]{M(2)},Marshal.SizeOf(typeof(INPUT))); for(int i=1;i<=10;i++){ SetCursorPos(x1+(x2-x1)*i/10, y1+(y2-y1)*i/10); System.Threading.Thread.Sleep(25);} SendInput(1,new[]{M(4)},Marshal.SizeOf(typeof(INPUT))); }
  public static string Cls(IntPtr h) { var s=new StringBuilder(256); GetClassName(h,s,256); return s.ToString(); }
  public static string Title(IntPtr h) { var s=new StringBuilder(256); GetWindowText(h,s,256); return s.ToString(); }
  public static string Proc(IntPtr h) { uint pid; GetWindowThreadProcessId(h,out pid); try { return Process.GetProcessById((int)pid).ProcessName; } catch { return "?"+pid; } }
  public static string Fg() { var h=GetForegroundWindow(); if(h==IntPtr.Zero) return "<无前台窗口>"; uint pid; uint tid=GetWindowThreadProcessId(h,out pid); var g=new GTI(); g.cb=Marshal.SizeOf(typeof(GTI)); string f=""; if(GetGUIThreadInfo(tid,ref g)) f=Cls(g.foc); return Proc(h)+" ["+Cls(h)+" / 焦点 "+f+"]"; }
  public static IntPtr Find(string procName, string cls) { IntPtr res=IntPtr.Zero; EnumWindows((h,l)=>{ if(!IsWindowVisible(h)) return true; if(Proc(h).Equals(procName,StringComparison.OrdinalIgnoreCase) && (cls==null||Cls(h)==cls)) { res=h; return false;} return true; }, IntPtr.Zero); return res; }
  public static List<IntPtr> All(string procName) { var l=new List<IntPtr>(); EnumWindows((h,x)=>{ if(IsWindowVisible(h) && Proc(h).Equals(procName,StringComparison.OrdinalIgnoreCase)) l.Add(h); return true; }, IntPtr.Zero); return l; }
  public static bool Activate(IntPtr h) { keybd_event(0x12,0,0,UIntPtr.Zero); keybd_event(0xE8,0,0,UIntPtr.Zero); keybd_event(0xE8,0,2,UIntPtr.Zero); ShowWindow(h,9); bool ok=SetForegroundWindow(h); keybd_event(0x12,0,2,UIntPtr.Zero); System.Threading.Thread.Sleep(350); return GetForegroundWindow()==h; }
}
