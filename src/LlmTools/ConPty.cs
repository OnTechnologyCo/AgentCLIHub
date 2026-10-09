using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LlmTools;

/// <summary>
/// Windows 가짜 콘솔(ConPTY)에 붙여 프로그램을 띄운다.
///
/// **왜 필요한가.** `claude` 를 대화형으로 쓰려면 그것이 진짜 터미널에 붙어 있다고 믿어야 한다.
/// 파이프로 리디렉션하면 TUI 가 뜨지 않고 색·커서·박스가 전부 사라진다. ConPTY 는 그 "진짜
/// 터미널"을 프로세스 없이 만들어 주는 것이고, 나오는 VT 시퀀스를 우리가 받아 xterm.js 로 그린다.
///
/// VS Code 통합 터미널이 정확히 이 구조다(ConPTY + xterm.js). 그쪽은 `node-pty` 를 끼우는데,
/// node-pty 가 Windows 에서 하는 일이 바로 이 파일이다. 여기는 C# 이라 중간층 없이 직접 부른다.
///
/// Windows 10 1809(빌드 17763) 이상. 그 아래는 <see cref="Available"/> 가 false 다.
/// </summary>
public sealed class ConPty : IDisposable
{
    // ── Win32 ───────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint flags, out IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hRead, out IntPtr hWrite, IntPtr sa, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr h, byte[] buf, int toRead, out int read, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr h, byte[] buf, int toWrite, out int written, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value,
                                                         IntPtr size, IntPtr prev, IntPtr ret);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta,
                                              bool inherit, uint flags, IntPtr env, string? cwd,
                                              ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr h, out uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr h, uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr h, uint ms);

    // **CharSet.Unicode 가 없으면 안 된다.** DllImport 의 기본은 Ansi 라 `string` 이
    // ANSI 로 마샬링되는데, 부르는 함수는 W(유니코드)다. "kernel32.dll" 이 바이트열로
    // 들어가 UTF-16 으로 읽히면 '湫...' 이 되어 조회가 실패하고 NULL 이 돌아온다.
    // 그러면 Available 이 항상 false 가 되어 "이 Windows 는 ConPTY 를 지원하지 않는다"는
    // 거짓 진단이 나온다 — Windows 11 에서도 그렇다.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr mod, string name);

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT   = 0x00000400;
    private const uint CREATE_NO_WINDOW             = 0x08000000;
    private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint STILL_ACTIVE = 259;

    /// <summary>이 PC 에서 ConPTY 를 쓸 수 있는가. Windows 10 1809 미만이면 false.</summary>
    public static bool Available
    {
        get
        {
            try
            {
                var k = GetModuleHandleW("kernel32.dll");
                return k != IntPtr.Zero && GetProcAddress(k, "CreatePseudoConsole") != IntPtr.Zero;
            }
            catch { return false; }
        }
    }

    // ── 상태 ────────────────────────────────────────────────────────────────
    private IntPtr _hpc = IntPtr.Zero;
    private IntPtr _inWrite = IntPtr.Zero;    // 우리가 쓴다 → 자식의 stdin
    private IntPtr _outRead = IntPtr.Zero;    // 자식의 stdout → 우리가 읽는다
    private IntPtr _proc = IntPtr.Zero;
    private IntPtr _thread = IntPtr.Zero;
    private IntPtr _attrList = IntPtr.Zero;
    private Thread? _reader;
    private volatile bool _closed;
    private short _cols, _rows;

    /// <summary>자식이 낸 바이트. **문자열이 아니다** — UTF-8 문자가 읽기 경계에서 잘릴 수 있다.</summary>
    public event Action<byte[]>? OnOutput;

    /// <summary>자식이 끝났다. 인자는 종료 코드.</summary>
    public event Action<int>? OnExit;

    public bool IsRunning => _proc != IntPtr.Zero && !_closed;
    public int ProcessId { get; private set; }

    /// <summary>
    /// 가짜 콘솔을 만들고 <paramref name="commandLine"/> 을 거기 붙여 띄운다.
    ///
    /// **핸들 정리 순서가 까다롭다.** ConPTY 는 넘겨받은 파이프 끝을 자기가 복제해 갖는다.
    /// 우리 쪽 사본을 안 닫으면 자식이 끝나도 읽기가 안 끝나 스레드가 매달린다.
    /// </summary>
    /// <param name="envOverlay">
    /// 우리 환경 위에 덮어쓸 값. 값이 null 인 항목은 **지운다**. null 을 넘기면 우리 환경 그대로.
    /// </param>
    public void Start(string commandLine, string? workingDir, short cols, short rows,
                      IReadOnlyDictionary<string, string?>? envOverlay = null)
    {
        if (!Available) throw new PlatformNotSupportedException(
            "이 Windows 버전은 가짜 콘솔(ConPTY)을 지원하지 않습니다. Windows 10 1809 이상이 필요합니다.");

        if (cols < 2) cols = 80;
        if (rows < 2) rows = 24;
        _cols = cols; _rows = rows;

        // 파이프 두 쌍. 이름이 헷갈리기 쉬우니 방향으로 적는다:
        //   inRead   → ConPTY 가 읽는다(= 자식의 stdin)
        //   outWrite → ConPTY 가 쓴다(= 자식의 stdout)
        if (!CreatePipe(out var inRead, out _inWrite, IntPtr.Zero, 0)) throw Fail("stdin 파이프");
        if (!CreatePipe(out _outRead, out var outWrite, IntPtr.Zero, 0)) throw Fail("stdout 파이프");

        var size = new COORD { X = cols, Y = rows };
        var hr = CreatePseudoConsole(size, inRead, outWrite, 0, out _hpc);
        if (hr != 0) throw new Win32Exception(hr, "가짜 콘솔 생성 실패");

        // **ConPTY 가 복제해 갔으므로 우리 사본은 즉시 닫는다.**
        CloseHandle(inRead);
        CloseHandle(outWrite);

        // 프로세스 속성 목록에 가짜 콘솔을 심는다. 이걸 안 하면 자식이 우리 콘솔
        // (없다 — WPF 앱이다)을 상속해 TUI 가 뜨지 않는다.
        var listSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);   // 크기만 물어본다
        _attrList = Marshal.AllocHGlobal(listSize);
        if (!InitializeProcThreadAttributeList(_attrList, 1, 0, ref listSize)) throw Fail("속성 목록");
        if (!UpdateProcThreadAttribute(_attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                                       _hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw Fail("가짜 콘솔 연결");

        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        si.lpAttributeList = _attrList;

        // CreateProcessW 는 lpCommandLine 을 **수정할 수 있다** — 리터럴을 넘기면 안 된다.
        //
        // ⚠ **CREATE_NO_WINDOW 를 걸면 안 된다.** 가짜 콘솔에 붙는 자식과 충돌해서
        // 자식은 뜨지만 출력 파이프가 곧바로 끊긴다 — ReadFile 이 109(ERROR_BROKEN_PIPE)를
        // 내고 화면에 한 글자도 안 나온다. 창이 안 뜨는 것은 가짜 콘솔 자체가 창이 없기
        // 때문이지 이 플래그 덕이 아니다. MS 표본도 EXTENDED_STARTUPINFO_PRESENT 만 건다.
        var cmd = new StringBuilder(commandLine);
        var envBlock = BuildEnvBlock(envOverlay);
        PROCESS_INFORMATION pi;
        bool started;
        int startError;
        try
        {
            started = CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                                     EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                                     envBlock, workingDir, ref si, out pi);
            startError = Marshal.GetLastWin32Error();
        }
        finally
        {
            if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
        }
        if (!started) throw Fail("프로세스 시작", startError);

        _proc = pi.hProcess;
        _thread = pi.hThread;
        ProcessId = pi.dwProcessId;

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "conpty-read" };
        _reader.Start();
    }

    /// <summary>
    /// 자식 출력을 계속 읽어 올린다.
    ///
    /// **바이트를 그대로 올린다.** 여기서 UTF-8 로 디코드하면 한 글자가 읽기 경계에 걸쳤을 때
    /// 깨진다 — 한글은 3바이트라 4096 경계에서 흔하게 걸린다. 디코딩은 조립이 끝난 뒤에 한다.
    /// </summary>
    private void ReadLoop()
    {
        var buf = new byte[8192];
        try
        {
            while (!_closed)
            {
                if (!ReadFile(_outRead, buf, buf.Length, out var n, IntPtr.Zero) || n <= 0) break;
                var slice = new byte[n];
                Buffer.BlockCopy(buf, 0, slice, 0, n);
                try { OnOutput?.Invoke(slice); } catch { }
            }
        }
        catch { }

        // 파이프가 끝났다 = 자식이 죽었다. 종료 코드를 확인해 알린다.
        var code = -1;
        try { if (GetExitCodeProcess(_proc, out var c) && c != STILL_ACTIVE) code = (int)c; } catch { }
        _closed = true;
        try { OnExit?.Invoke(code); } catch { }
    }

    /// <summary>키 입력을 자식에게 보낸다. UTF-8 로 인코딩해 그대로 밀어 넣는다.</summary>
    public void Write(string s)
    {
        if (_closed || _inWrite == IntPtr.Zero || string.IsNullOrEmpty(s)) return;
        var bytes = Encoding.UTF8.GetBytes(s);
        try { WriteFile(_inWrite, bytes, bytes.Length, out _, IntPtr.Zero); } catch { }
    }

    /// <summary>
    /// 창 크기 변경을 알린다. **이걸 안 하면 TUI 가 옛 크기로 그린다** —
    /// 줄이 접히거나 박스가 어긋나 보이는 원인이 대부분 이것이다.
    /// </summary>
    public void Resize(short cols, short rows)
    {
        if (_closed || _hpc == IntPtr.Zero) return;
        if (cols < 2 || rows < 2) return;
        if (cols == _cols && rows == _rows) return;
        try
        {
            if (ResizePseudoConsole(_hpc, new COORD { X = cols, Y = rows }) >= 0)
            { _cols = cols; _rows = rows; }
        }
        catch { }
    }

    /// <summary>
    /// 자식을 끝내고 자원을 회수한다.
    ///
    /// 순서: **가짜 콘솔을 먼저 닫는다.** 그러면 자식이 EOF 를 보고 스스로 나가는 경우가 많아
    /// 강제 종료 없이 끝난다(CLI 는 세션을 저장할 것이 있다). 그래도 안 나가면 그때 죽인다.
    /// </summary>
    public void Dispose()
    {
        if (_closed && _hpc == IntPtr.Zero) return;
        _closed = true;

        if (_hpc != IntPtr.Zero) { try { ClosePseudoConsole(_hpc); } catch { } _hpc = IntPtr.Zero; }

        if (_proc != IntPtr.Zero)
        {
            try
            {
                if (WaitForSingleObject(_proc, 2000) != 0) TerminateProcess(_proc, 1);
            }
            catch { }
            CloseHandle(_proc); _proc = IntPtr.Zero;
        }
        if (_thread != IntPtr.Zero) { CloseHandle(_thread); _thread = IntPtr.Zero; }
        if (_inWrite != IntPtr.Zero) { CloseHandle(_inWrite); _inWrite = IntPtr.Zero; }
        if (_outRead != IntPtr.Zero) { CloseHandle(_outRead); _outRead = IntPtr.Zero; }
        if (_attrList != IntPtr.Zero)
        {
            try { DeleteProcThreadAttributeList(_attrList); } catch { }
            Marshal.FreeHGlobal(_attrList); _attrList = IntPtr.Zero;
        }
    }

    /// <summary>
    /// 환경 블록을 만든다. 형식은 `이름=값\0이름=값\0\0` 의 UTF-16 이고,
    /// **이름 순으로 정렬해야 한다** — Windows 가 그렇게 기대한다.
    ///
    /// 자식에게 `TERM` 같은 것을 알려주려면 이것이 필요하다. NULL 을 넘기면 우리 환경이
    /// 그대로 복제될 뿐이라 TUI 는 자기가 어떤 단말에 붙었는지 알 방법이 없다.
    /// </summary>
    private static IntPtr BuildEnvBlock(IReadOnlyDictionary<string, string?>? overlay)
    {
        if (overlay is null || overlay.Count == 0) return IntPtr.Zero;

        var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var k = e.Key?.ToString();
            if (!string.IsNullOrEmpty(k)) map[k] = e.Value?.ToString() ?? "";
        }
        foreach (var (k, v) in overlay)
        {
            if (string.IsNullOrEmpty(k)) continue;
            if (v is null) map.Remove(k);   // 값이 null 이면 지우라는 뜻
            else map[k] = v;
        }

        var sb = new StringBuilder();
        foreach (var (k, v) in map)
        {
            // `=C:` 같은 드라이브별 현재 폴더 항목은 이름이 '=' 로 시작한다. 넘기면 블록이 깨진다.
            if (k.StartsWith('=')) continue;
            sb.Append(k).Append('=').Append(v).Append('\0');
        }
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    private static Win32Exception Fail(string what, int? error = null)
    {
        var code = error ?? Marshal.GetLastWin32Error();
        return new Win32Exception(code, $"{what} 실패 (Windows 오류 {code}: {new Win32Exception(code).Message})");
    }
}
