param([string]$LuaDll = (Join-Path $PSScriptRoot '..\bin\lua51.dll'))
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class AutoReloadLuaTest {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("lua51.dll", CallingConvention=CallingConvention.Cdecl)] static extern IntPtr luaL_newstate();
    [DllImport("lua51.dll", CallingConvention=CallingConvention.Cdecl)] static extern void luaL_openlibs(IntPtr state);
    [DllImport("lua51.dll", CallingConvention=CallingConvention.Cdecl)] static extern int luaL_loadbuffer(IntPtr state, byte[] source, UIntPtr length, string name);
    [DllImport("lua51.dll", CallingConvention=CallingConvention.Cdecl)] static extern int lua_pcall(IntPtr state, int args, int results, int handler);
    [DllImport("lua51.dll", CallingConvention=CallingConvention.Cdecl)] static extern IntPtr lua_tolstring(IntPtr state, int index, out UIntPtr length);
    [DllImport("lua51.dll", CallingConvention=CallingConvention.Cdecl)] static extern void lua_close(IntPtr state);
    public static void Run(string dll, string source) {
        if (LoadLibraryExW(dll, IntPtr.Zero, 8) == IntPtr.Zero) throw new Exception("LuaJIT library load failed: " + Marshal.GetLastWin32Error());
        var state = luaL_newstate();
        if (state == IntPtr.Zero) throw new Exception("Lua allocation failed");
        try {
            luaL_openlibs(state);
            var bytes = Encoding.UTF8.GetBytes(source);
            int result = luaL_loadbuffer(state, bytes, (UIntPtr)bytes.Length, "@AutoReloadTests");
            if (result == 0) result = lua_pcall(state, 0, -1, 0);
            if (result != 0) {
                UIntPtr length;
                var text = lua_tolstring(state, -1, out length);
                var data = new byte[(int)length.ToUInt64()];
                Marshal.Copy(text, data, 0, data.Length);
                throw new Exception(Encoding.UTF8.GetString(data));
            }
        } finally { lua_close(state); }
    }
}
'@
$previous = Get-Location
$nativeDirectory = [Environment]::CurrentDirectory
try {
    Set-Location -LiteralPath $PSScriptRoot
    [Environment]::CurrentDirectory = $PSScriptRoot
    [AutoReloadLuaTest]::Run((Resolve-Path -LiteralPath $LuaDll).Path, (Get-Content -LiteralPath './test.lua' -Raw -Encoding UTF8))
} finally {
    [Environment]::CurrentDirectory = $nativeDirectory
    Set-Location -LiteralPath $previous.Path
}
