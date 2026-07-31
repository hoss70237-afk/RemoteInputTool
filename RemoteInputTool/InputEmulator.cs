using System;
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
        struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; }
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        const uint INPUT_MOUSE = 0;
        const uint MOUSEEVENTF_MOVE = 0x0001;
        const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        const uint MOUSEEVENTF_LEFTUP = 0x0004;
        const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        const uint MOUSEEVENTF_WHEEL = 0x0800;

        public static void MoveMouse(double ratioX, double ratioY)
        {
            int screenW = (int)SystemParameters.VirtualScreenWidth;
            int screenH = (int)SystemParameters.VirtualScreenHeight;
            int dx = (int)(ratioX * 65535);
            int dy = (int)(ratioY * 65535);
            SendMouseInput(dx, dy, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
        }

        public static void MoveMouseRel(int dx, int dy)
        {
            SendMouseInput(dx, dy, MOUSEEVENTF_MOVE);
        }

        public static void Click(string button)
        {
            uint down = button == "left" ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_RIGHTDOWN;
            uint up = button == "left" ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_RIGHTUP;
            SendMouseInput(0, 0, down);
            SendMouseInput(0, 0, up);
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
            input.u.mi.dx = dx;
            input.u.mi.dy = dy;
            input.u.mi.dwFlags = flags;
            SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }
    }
}
