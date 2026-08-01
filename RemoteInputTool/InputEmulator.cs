using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;

namespace RemoteInputTool
{
    public static class InputEmulator
    {
        [DllImport("user32.dll")]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public InputUnion u; }
        
        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion { 
            [FieldOffset(0)] public MOUSEINPUT mi; 
            [FieldOffset(0)] public KEYBDINPUT ki; 
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        const uint INPUT_MOUSE = 0;
        const uint INPUT_KEYBOARD = 1;

        const uint MOUSEEVENTF_MOVE = 0x0001;
        const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        const uint MOUSEEVENTF_LEFTUP = 0x0004;
        const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        const uint MOUSEEVENTF_WHEEL = 0x0800;

        const uint KEYEVENTF_KEYUP = 0x0002;

        public static void MoveMouse(double ratioX, double ratioY, Rect area)
        {
            double screenW = SystemParameters.VirtualScreenWidth * MainWindow.DpiX;
            double screenH = SystemParameters.VirtualScreenHeight * MainWindow.DpiY;
            double screenLeft = SystemParameters.VirtualScreenLeft * MainWindow.DpiX;
            double screenTop = SystemParameters.VirtualScreenTop * MainWindow.DpiY;
            
            double targetPx = area.X + (area.Width * ratioX);
            double targetPy = area.Y + (area.Height * ratioY);
            
            // Windows API の絶対座標は 65536 をかけるのが正しい仕様
            int dx = (int)Math.Round(((targetPx - screenLeft) / screenW) * 65536.0);
            int dy = (int)Math.Round(((targetPy - screenTop) / screenH) * 65536.0);
            
            if (dx > 65535) dx = 65535;
            if (dy > 65535) dy = 65535;
            if (dx < 0) dx = 0;
            if (dy < 0) dy = 0;

            SendMouseInput(dx, dy, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
        }

        public static void MoveMouseRel(int dx, int dy) => SendMouseInput(dx, dy, MOUSEEVENTF_MOVE);
        
        public static void Click(string button)
        {
            uint down = MOUSEEVENTF_LEFTDOWN, up = MOUSEEVENTF_LEFTUP;
            string b = button.ToLower();
            if (b == "right") { down = MOUSEEVENTF_RIGHTDOWN; up = MOUSEEVENTF_RIGHTUP; }
            else if (b == "middle") { down = MOUSEEVENTF_MIDDLEDOWN; up = MOUSEEVENTF_MIDDLEUP; }
            SendMouseInput(0, 0, down); SendMouseInput(0, 0, up);
        }

        public static void Drag(string state)
        {
            uint flag = state == "down" ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
            SendMouseInput(0, 0, flag);
        }

        public static void Scroll(int value)
        {
            var input = new INPUT { type = INPUT_MOUSE };
            input.u.mi.mouseData = (uint)value;
            input.u.mi.dwFlags = MOUSEEVENTF_WHEEL;
            SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        private static void SendMouseInput(int dx, int dy, uint flags)
        {
            var input = new INPUT { type = INPUT_MOUSE };
            input.u.mi.dx = dx; input.u.mi.dy = dy; input.u.mi.dwFlags = flags;
            SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        public static void SendKey(string keyData)
        {
            if (string.IsNullOrWhiteSpace(keyData)) return;
            var keys = keyData.Split('+');
            List<ushort> vkeys = new List<ushort>();
            foreach(var k in keys) {
                string t = k.Trim().ToLower();
                ushort vk = 0;
                switch(t) {
                    case "ctrl": case "control": vk = 0x11; break;
                    case "shift": vk = 0x10; break;
                    case "alt": vk = 0x12; break;
                    case "enter": vk = 0x0D; break;
                    case "space": vk = 0x20; break;
                    case "tab": vk = 0x09; break;
                    case "esc": case "escape": vk = 0x1B; break;
                    case "win": case "windows": vk = 0x5B; break;
                    case "back": case "backspace": vk = 0x08; break;
                    case "del": case "delete": vk = 0x2E; break;
                    case "up": vk = 0x26; break;
                    case "down": vk = 0x28; break;
                    case "left": vk = 0x25; break;
                    case "right": vk = 0x27; break;
                    default:
                        if (t.Length == 1) vk = (ushort)char.ToUpper(t[0]);
                        break;
                }
                if(vk != 0) vkeys.Add(vk);
            }

            if(vkeys.Count == 0) return;

            var inputs = new List<INPUT>();
            foreach(var vk in vkeys) {
                var input = new INPUT { type = INPUT_KEYBOARD };
                input.u.ki.wVk = vk;
                inputs.Add(input);
            }
            for(int i = vkeys.Count - 1; i >= 0; i--) {
                var input = new INPUT { type = INPUT_KEYBOARD };
                input.u.ki.wVk = vkeys[i];
                input.u.ki.dwFlags = KEYEVENTF_KEYUP;
                inputs.Add(input);
            }

            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        }
    }
}
