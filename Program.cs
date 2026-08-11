using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using SDL2;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace HD2_Helper
{
    internal class Program
    {
        private const string UpdaterFileName = "HD2 Helper Updater.exe";
        private const string UpdaterMutexName = "HD2_Helper_Updater_Unique";
        private const string StartedByUpdaterArgument = "--started-by-updater";
        private const string EmbedParentArgumentPrefix = "--embed-parent=";

        [STAThread]
        static void Main(string[] args)
        {
            bool isSteamAutostart = args.Any(arg => arg.Equals("--steam-autostart", StringComparison.OrdinalIgnoreCase));
            IntPtr embedParentHandle = ParseEmbedParentHandle(args);
            bool startedByUpdater = args.Any(arg => arg.Equals(StartedByUpdaterArgument, StringComparison.OrdinalIgnoreCase)) || IsUpdaterRunning();

            // 기존 헬퍼 바로가기와 Steam 실행 옵션을 유지하면서 업데이트 확인을 항상 먼저 거치게 한다.
            // 업데이터가 다시 실행한 헬퍼는 식별 인수로 이 분기를 건너뛰어 상호 실행 반복을 막는다.
            if (!startedByUpdater && TryLaunchUpdater(isSteamAutostart))
                return;

            using (Mutex mutex = new Mutex(true, "HD2_Helper_Unique", out bool createdNew))
            {
                if (!createdNew)
                {
                    // Steam 실행 옵션으로 매번 호출될 때는 이미 실행 중이어도 알림창 없이 조용히 빠진다.
                    if (!isSteamAutostart)
                        MessageBox.Show("이미 프로그램이 실행 중입니다.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                    return;
                }

                NativeLibrary.SetDllImportResolver(typeof(SDL).Assembly, (libraryName, assembly, searchPath) =>
                {
                    if (libraryName == "SDL2")
                        return NativeLibrary.Load("SDL2.dll", assembly, searchPath);
                    return IntPtr.Zero;
                });

                ApplicationConfiguration.Initialize();
                MainForm mainForm = new MainForm(embedParentHandle);
                Application.Run();
            }
        }

        private static IntPtr ParseEmbedParentHandle(string[] args)
        {
            string? value = args.FirstOrDefault(arg => arg.StartsWith(EmbedParentArgumentPrefix, StringComparison.OrdinalIgnoreCase));
            if (value == null || !long.TryParse(value[EmbedParentArgumentPrefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long handleValue))
                return IntPtr.Zero;

            return new IntPtr(handleValue);
        }

        private static bool TryLaunchUpdater(bool isSteamAutostart)
        {
            string updaterPath = Path.Combine(AppContext.BaseDirectory, UpdaterFileName);
            if (!File.Exists(updaterPath))
                return false;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = updaterPath,
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = true
                };

                // Steam이 중복 호출한 경우 업데이터도 별도 알림 없이 기존 인스턴스를 사용한다.
                if (isSteamAutostart)
                    startInfo.ArgumentList.Add("--steam-autostart");

                Process.Start(startInfo);
                return true;
            }
            catch
            {
                // 업데이터가 없거나 시작하지 못해도 사용자가 기존 헬퍼를 계속 쓸 수 있게 한다.
                return false;
            }
        }

        private static bool IsUpdaterRunning()
        {
            try
            {
                // 식별 인수를 전달하지 않는 이전 업데이터도 실행 중 뮤텍스로 판별해 재호출 반복을 방지한다.
                using Mutex updaterMutex = Mutex.OpenExisting(UpdaterMutexName);
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }
    }

    public class MainForm : Form
    {
        private static readonly string AppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HD2 Helper");
        private static readonly string SettingsPath = Path.Combine(AppDataPath, "settings.ini");
        private static readonly string DisabledItemsPath = Path.Combine(AppDataPath, "disabled.ini");
        private static readonly string PresetsPath = Path.Combine(AppDataPath, "presets.json");
        private static readonly string PresetBackupPath = Path.Combine(AppDataPath, "preset-backups");
        private static readonly string CrosshairPath = Path.Combine(AppDataPath, "crosshair.json");
        private static readonly string SupportWeaponPath = Path.Combine(AppDataPath, "supportWeapon.json");

        private WebView2? _webView;
        private readonly List<WebView2> _webViews = new();
        private OverlayForm? _overlayForm;
        private PresetOverlayForm? _presetOverlayForm;
        private HelperEditorWindow? _helperEditorWindow;
        private OcrDebugOverlayForm? _ocrDebugOverlayForm;
        private OcrRegionOverlayForm? _ocrRegionOverlayForm;
        private StratagemSelectionDebugForm? _stratagemSelectionDebugForm;
        private SoftwareCursorOverlayForm? _softwareCursorOverlayForm;
        private IntPtr _softwareCursorAnchorHandle = IntPtr.Zero;
        private bool _helperEditorWindowRequestedVisible;
        private bool _presetOverlayRequestedVisible;

        private CrosshairForm? _crosshairForm;
        private SupportWeaponGaugeForm? _supportWeaponGaugeForm;
        private CrosshairEditorForm? _crosshairEditorForm;
        private OcrRegionSettingsForm? _ocrRegionSettingsForm;
        private AutoReloadCalibrationForm? _autoReloadCalibrationForm;
        private System.Windows.Forms.Timer? _crosshairTimer;
        private System.Windows.Forms.Timer? _supportWeaponGaugeTimer;
        private System.Windows.Forms.Timer? _autoReloadDetectionTimer;
        private System.Windows.Forms.Timer? _inactiveGameAudioMuteTimer;
        private System.Windows.Forms.Timer? _softwareCursorTimer;
        private CancellationTokenSource? _padLoopCts;
        private CancellationTokenSource? _autoSelectionCts;
        private InputHookManager? _inputHook;
        private OcrEngine? _ocrEngine;
        // 자동선택과 자동 재장전이 같은 OCR 엔진을 동시에 호출하지 않도록 직렬화한다.
        private readonly SemaphoreSlim _ocrRecognitionGate = new(1, 1);
        private Point _cachedGameClientCenter = Point.Empty;
        private bool _hasCachedGameClientCenter;
        private readonly IntPtr _embeddedParentHandle;

        private const int CrosshairOverlayRefreshIntervalMs = 100;
        private const int SupportWeaponGaugeRefreshIntervalMs = 25;

        private const int BaseClientWidth = 950;
        private const int ExpandedSettingsClientWidth = 1060;
        // 장비 프리셋 탭 행을 추가했으므로 편집창과 원본 창의 기준 높이도 함께 늘린다.
        private const int BaseClientHeight = 630;
        // 기본 10칸 뒤의 보관용 슬롯은 한 줄에 5칸씩 늘어나며, 지나치게 큰 편집창을 막기 위해 20칸까지 허용한다.
        private const int BaseStratagemSlotCount = 10;
        private const int MaxAdditionalStratagemSlots = 20;
        private const int StratagemSlotsPerRow = 5;
        private const int StratagemSlotRowHeight = 130;
        private const int BaseReferenceWidth = 1920;
        private const int BaseReferenceHeight = 1080;
        private const double MinClientScale = 0.5;
        private const double StratagemIconFallbackMinScore = 0.65;
        private int _layoutClientWidth = BaseClientWidth;
        private bool _isAdjustingClientSize;

        private static List<(string Type, string Category, string Name)> _parsedData = new();
        private static Dictionary<string, Image?> _imageCache = new();
        private static Dictionary<string, string[]> _sequenceMap = new();

        private static int _additionalStratagemSlots;
        private static int StratagemSlotCount => BaseStratagemSlotCount + _additionalStratagemSlots;
        private static int AdditionalStratagemSlotRows => (int)Math.Ceiling(StratagemSlotCount / (double)StratagemSlotsPerRow) - (BaseStratagemSlotCount / StratagemSlotsPerRow);
        private static int CurrentBaseClientHeight => BaseClientHeight + (AdditionalStratagemSlotRows * StratagemSlotRowHeight);

        private static string?[] _currentSlots = new string?[BaseStratagemSlotCount];
        private static string?[] _currentLoadoutSlots = new string?[4];
        private static string?[] _previousStratagemSlots = new string?[BaseStratagemSlotCount];
        // 오버레이 휠에서 보이는 슬롯만 제어하며 자동선택용 _currentSlots 순서는 유지한다.
        private static bool[] _overlaySlotVisibility = CreateDefaultOverlaySlotVisibility();
        private static HashSet<string> _disabledItems = new();

        private static readonly Dictionary<int, uint> _slotKey = new();
        private static readonly Dictionary<uint, (string Trigger, float Threshold)> _mouseKey = new();

        private static string _stratagemType = "Hold";
        private static readonly Dictionary<string, uint> _stratagemKey = new()
        {
            ["start"] = (uint)Keys.LControlKey,
            ["up"] = (uint)Keys.W,
            ["down"] = (uint)Keys.S,
            ["left"] = (uint)Keys.A,
            ["right"] = (uint)Keys.D,
        };
        // 값이 없는 방향은 게임 input_settings.config에서 읽은 _stratagemKey 값을 그대로 사용한다.
        private static readonly Dictionary<string, uint> _manualStratagemKey = new(StringComparer.OrdinalIgnoreCase);

        private static int _inputDelay = 30;
        private static uint _autoSelectKey = (uint)Keys.F1;
        private static uint _overlayKey = (uint)Keys.MButton;
        private static uint _reinforceKey = (uint)Keys.XButton1;
        private static uint _stratagemComboKey = (uint)Keys.CapsLock;
        private static uint _crosshairToggleKey = 0;
        private static uint _helperEditorKey = (uint)Keys.F3;
        private static uint _presetOverlayKey = (uint)Keys.F4;
        private static uint _chatKey = (uint)Keys.Enter;
        private static bool _stratagemCompactLayout;
        // 장비 선택창은 새 그리드형과 기존 목록형 중 사용자가 고른 형태를 모든 WebView에 동일하게 적용한다.
        private static bool _useLegacyEquipmentLayout;
        private static bool _stratagemReselectEnabled = true;
        // 기본 OFF: 사용자가 수동 저장을 선택할 때까지 프리셋 파일은 바꾸지 않고, 두 WebView의 화면만 동기화한다.
        private static bool _presetAutoSaveEnabled;
        private static bool _testModeEnabled;
        private static bool _pauseCrosshairTimer;
        private static bool _pauseSupportWeaponTimer;
        private static bool _pauseSoftwareCursorTimer;
        private static bool _pauseAudioMuteTimer;
        private static bool _pauseGamepadLoop;
        private static bool _muteGameAudioWhenInactive;
        private static bool _excludeOverlaysFromCapture;
        // 기본 OFF: 중앙의 "무기 장전" 안내가 보일 때만 공격 입력에 재장전을 보조한다.
        private static bool _autoReloadEnabled;
        private static AutoReloadSettings _autoReloadSettings = new();
        private static string _selectedPresetId = "";
        // 스트라타젬과 장비는 독립적으로 조합할 수 있으므로 마지막 선택값도 각각 보존한다.
        private static string _selectedEquipmentPresetId = "";
        private static CrosshairSettings _crosshairSettings = new();
        private static SupportWeaponAssistSettings _supportWeaponSettings = new();
        private static readonly object SupportWarningMediaPlayersLock = new();
        private static readonly List<Windows.Media.Playback.MediaPlayer> SupportWarningMediaPlayers = new();
        private static readonly Dictionary<string, OcrRegionSettings> _ocrRegionSettings = CreateDefaultOcrRegionSettings();
        private const string KoreanOcrLanguageTag = "ko-KR";
        private static string _lastOcrMatchDebugLine = "";
        private static string _lastIconMatchDebugLine = "";
        private static bool _isChat;
        private static bool _isPad;
        private static bool _isWaitingForKey;
        private static string? _waitingKeyTarget;
        private int _isSending = 0;
        private uint _heldOverlayStratagemViewKey = 0;

        private sealed class InputConfigCandidate
        {
            public required FileInfo File { get; init; }
            public required int Priority { get; init; }
            public required string Source { get; init; }
            public long AccountTimestamp { get; init; }
        }


        private readonly HangulEngine _editorHangulEngine = new();
        private string _editorLastInjected = "";
        private bool _editorHangulMode = true;
        private bool _editorForwardedCtrlDown;
        private bool _editorForwardedShiftDown;

        private bool _isRightMouseButtonDown;
        private bool _isLeftMouseButtonDown;
        private DateTime _leftMouseDownStartedAt = DateTime.MinValue;
        private bool _supportWeaponWarningPlayed;
        private bool _supportWeaponAutoReleased;
        private bool _supportWeaponSuppressLeftUntilRelease;
        private bool _supportWeaponPausedByWeaponKey;
        private string _supportWeaponMode = "Off";
        private bool? _lastObservedGameActive;
        private readonly Dictionary<string, bool> _gameAudioMuteStatesBeforeHelper = new();
        private DateTime _lastAutoReloadAttemptAt = DateTime.MinValue;
        private AutoReloadDetectionResult _lastAutoReloadDetection = AutoReloadDetectionResult.Empty;
        // 발사 시작과 해제 뒤의 검사가 겹쳐도 OCR 판독과 R 입력 판단은 순서대로 한 번씩 처리한다.
        private readonly SemaphoreSlim _autoReloadCheckGate = new(1, 1);

        public class InputEventArgs : EventArgs
        {
            public uint VirtualKey { get; }
            public bool IsDown { get; }
            public bool IsInjected { get; }
            public InputEventArgs(uint vk, bool isDown, bool isInjected = false)
            {
                VirtualKey = vk;
                IsDown = isDown;
                IsInjected = isInjected;
            }
        }

        public enum PadButton : uint
        {
            L1 = 0x1001, R1, L2, R2, L3, R3,
            DUp, DDown, DLeft, DRight,
            PadA, PadB, PadX, PadY,
            PadStart, PadBack,
        }

        #region WinAPI
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr hWnd, out Rectangle lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr newLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref Point lpPoint);

        [DllImport("user32.dll")]
        private static extern void keybd_event(uint bVk, uint bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int mciSendString(string command, StringBuilder? returnValue, int returnLength, IntPtr winHandle);

        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        private const uint WDA_NONE = 0x00000000;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
        private const int GWL_STYLE = -16;
        private const long WS_CHILD = 0x40000000L;
        private const long WS_POPUP = 0x80000000L;
        private const long WS_CAPTION = 0x00C00000L;
        private const long WS_THICKFRAME = 0x00040000L;
        private const long WS_BORDER = 0x00800000L;
        private const long WS_DLGFRAME = 0x00400000L;
        #endregion

        public MainForm(IntPtr embeddedParentHandle = default)
        {
            _embeddedParentHandle = embeddedParentHandle;
            bool willEmbedIntoUpdater = embeddedParentHandle != IntPtr.Zero;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Text = "헬다이버즈2 보조 기구";
            // 임베드 실행은 표시 전에 테두리와 시작 위치를 확정해 화면 우측 하단에 독립 창이 잠깐 생기지 않게 한다.
            FormBorderStyle = willEmbedIntoUpdater ? FormBorderStyle.None : FormBorderStyle.Sizable;
            MaximizeBox = false;
            BackColor = Color.FromArgb(0x22, 0x22, 0x22);
            ClientSize = GetInitialClientSize();
            MinimumSize = SizeFromClientSize(new Size((int)Math.Round(BaseClientWidth * MinClientScale), (int)Math.Round(CurrentBaseClientHeight * MinClientScale)));
            Resize += (_, _) => KeepClientAspectRatio();

            StartPosition = FormStartPosition.Manual;
            if (willEmbedIntoUpdater)
            {
                Location = Point.Empty;
            }
            else
            {
                Rectangle area = Screen.PrimaryScreen!.WorkingArea;
                Left = area.Left + (area.Width - Width) / 2;
                Top = area.Top + (area.Height - Height) / 2;
            }

            _webView = new WebView2();
            _webView.Dock = DockStyle.Fill;
            _webView.DefaultBackgroundColor = Color.FromArgb(0x22, 0x22, 0x22);
            Controls.Add(_webView);

            Initialization();
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;
            const int HTTOP = 12;
            const int HTBOTTOM = 15;
            const int HTTOPLEFT = 13;
            const int HTTOPRIGHT = 14;
            const int HTBOTTOMLEFT = 16;
            const int HTBOTTOMRIGHT = 17;

            base.WndProc(ref m);

            if (m.Msg == WM_NCHITTEST)
            {
                int result = m.Result.ToInt32();

                if (result == HTTOP || result == HTBOTTOM ||
                    result == HTTOPLEFT || result == HTBOTTOMLEFT ||
                    result == HTTOPRIGHT || result == HTBOTTOMRIGHT)
                    m.Result = (IntPtr)1;
            }
        }

        private static Size GetInitialClientSize()
        {
            Rectangle bounds = Screen.PrimaryScreen!.Bounds;
            double scale = Math.Min(
                (double)bounds.Width / BaseReferenceWidth,
                (double)bounds.Height / BaseReferenceHeight
            );

            scale = Math.Max(scale, MinClientScale);
            return new Size(
                (int)Math.Round(BaseClientWidth * scale),
                (int)Math.Round(CurrentBaseClientHeight * scale)
            );
        }

        private void KeepClientAspectRatio()
        {
            if (_isAdjustingClientSize || WindowState != FormWindowState.Normal) return;

            int width = ClientSize.Width;
            int height = ClientSize.Height;
            if (width <= 0 || height <= 0) return;

            int minWidth = (int)Math.Round(_layoutClientWidth * MinClientScale);
            int minHeight = (int)Math.Round(CurrentBaseClientHeight * MinClientScale);
            double targetRatio = (double)_layoutClientWidth / CurrentBaseClientHeight;
            width = Math.Max(width, minWidth);
            height = Math.Max(height, minHeight);
            int targetHeight = (int)Math.Round(width / targetRatio);
            int targetWidth = (int)Math.Round(height * targetRatio);

            Size nextSize = Math.Abs(targetHeight - height) <= Math.Abs(targetWidth - width)
                ? new Size(width, targetHeight)
                : new Size(targetWidth, height);

            if (nextSize == ClientSize) return;

            _isAdjustingClientSize = true;
            ApplyClientSizePreservingEmbeddedOrigin(nextSize);
            _isAdjustingClientSize = false;
        }

        private void AdjustSettingsPanelClientWidth(WebView2? sourceWebView, bool collapsed)
        {
            int targetBaseWidth = collapsed ? BaseClientWidth : ExpandedSettingsClientWidth;

            if (ReferenceEquals(sourceWebView, _webView))
            {
                // 설정 패널 펼침 상태는 HTML 기준 폭과 실제 WinForms 창 폭을 같이 맞춰 글자 잘림을 줄인다.
                ApplyScaledClientWidth(targetBaseWidth);
                return;
            }

            if (sourceWebView?.FindForm() is HelperEditorWindow editorWindow)
                editorWindow.ApplySettingsPanelClientWidth(targetBaseWidth);
        }

        private void ApplyScaledClientWidth(int targetBaseWidth)
        {
            if (WindowState != FormWindowState.Normal)
            {
                _layoutClientWidth = targetBaseWidth;
                return;
            }

            double scale = ClientSize.Height > 0
                ? Math.Max(MinClientScale, (double)ClientSize.Height / CurrentBaseClientHeight)
                : 1.0;
            Size nextSize = new(
                (int)Math.Round(targetBaseWidth * scale),
                (int)Math.Round(CurrentBaseClientHeight * scale)
            );

            _layoutClientWidth = targetBaseWidth;
            MinimumSize = SizeFromClientSize(new Size(
                (int)Math.Round(targetBaseWidth * MinClientScale),
                (int)Math.Round(CurrentBaseClientHeight * MinClientScale)
            ));

            if (ClientSize == nextSize) return;

            _isAdjustingClientSize = true;
            ApplyClientSizePreservingEmbeddedOrigin(nextSize);
            _isAdjustingClientSize = false;
        }

        private void ApplyScaledClientHeight()
        {
            if (WindowState != FormWindowState.Normal)
                return;

            // 슬롯 행을 추가해도 현재 창의 가로 배율은 유지하고 세로 길이만 새 기준 높이에 맞춘다.
            double scale = ClientSize.Width > 0
                ? Math.Max(MinClientScale, (double)ClientSize.Width / _layoutClientWidth)
                : 1.0;
            Size nextSize = new(
                (int)Math.Round(_layoutClientWidth * scale),
                (int)Math.Round(CurrentBaseClientHeight * scale)
            );

            MinimumSize = SizeFromClientSize(new Size(
                (int)Math.Round(_layoutClientWidth * MinClientScale),
                (int)Math.Round(CurrentBaseClientHeight * MinClientScale)
            ));

            if (ClientSize == nextSize) return;

            _isAdjustingClientSize = true;
            ApplyClientSizePreservingEmbeddedOrigin(nextSize);
            _isAdjustingClientSize = false;
        }

        private void AdjustStratagemSlotClientHeight(WebView2? sourceWebView)
        {
            if (ReferenceEquals(sourceWebView, _webView))
            {
                ApplyScaledClientHeight();
                return;
            }

            if (sourceWebView?.FindForm() is HelperEditorWindow editorWindow)
                editorWindow.ApplyStratagemSlotClientHeight();
        }

        private void ApplyClientSizePreservingEmbeddedOrigin(Size nextClientSize)
        {
            if (_embeddedParentHandle == IntPtr.Zero)
            {
                ClientSize = nextClientSize;
                return;
            }

            // 테두리 없는 자식 창에서는 창 크기와 클라이언트 크기가 같으므로 관리 좌표도 항상 부모 원점으로 갱신한다.
            SetBounds(0, 0, nextClientSize.Width, nextClientSize.Height, BoundsSpecified.All);
        }

        private async void Initialization()
        {
            // 데이터 불러오기
            LoadDatabase();
            LoadUserSetting();
            LoadSetting();
            // 저장된 여분 슬롯 수는 창이 만들어진 뒤 읽히므로, 시작할 때도 슬롯 행 수에 맞춰 높이를 보정한다.
            ApplyScaledClientHeight();
            LoadCrosshairSettings();
            LoadSupportWeaponSettings();
            _supportWeaponMode = _supportWeaponSettings.Normalized().Mode;

            // 입력 처리
            this.Shown += (s, e) => EnsureInputHook();
            EnsureInputHook();

            // WebView 이미지 준비 메시지가 오지 않아도 프로세스만 떠 있는 상태가 되지 않도록 본창을 먼저 표시한다.
            Show();
            if (_embeddedParentHandle != IntPtr.Zero)
                EmbedIntoUpdaterHost();
            else
                Activate();

            ApplyCaptureExclusionToHelperWindows();
            // 패드
            await GamepadReader.InitializeAsync();
            RefreshPadLoopState();

            // OCR
            WarmupOcr();

            // 조준점
            InitializeCrosshairOverlay();
            InitializeAutoReloadDetection();
            InitializeInactiveGameAudioMute();
            InitializeSoftwareCursorOverlay();

            // 웹뷰
            InitializeWebView();
        }

        private void EmbedIntoUpdaterHost()
        {
            if (!IsWindow(_embeddedParentHandle))
                throw new InvalidOperationException("업데이터의 헬퍼 표시 영역을 찾지 못했습니다.");

            // 창을 소유한 헬퍼 프로세스가 직접 스타일과 부모를 바꿔 UIPI 권한 차이로 인한 Access denied를 피한다.
            long style = GetWindowLongPtr(Handle, GWL_STYLE).ToInt64();
            style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME);
            style |= WS_CHILD;
            SetWindowLongPtr(Handle, GWL_STYLE, new IntPtr(style));

            Marshal.SetLastPInvokeError(0);
            SetParent(Handle, _embeddedParentHandle);
            int parentError = Marshal.GetLastPInvokeError();
            if (GetParent(Handle) != _embeddedParentHandle)
                throw new System.ComponentModel.Win32Exception(parentError, "헬퍼 창을 업데이터 안에 배치하지 못했습니다.");

            if (!GetClientRect(_embeddedParentHandle, out Rectangle parentClient) || parentClient.Width <= 0 || parentClient.Height <= 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "업데이터 표시 영역의 크기를 읽지 못했습니다.");

            if (!MoveWindow(Handle, 0, 0, parentClient.Width, parentClient.Height, true))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "헬퍼 창 크기를 업데이터에 맞추지 못했습니다.");

            SetBounds(0, 0, parentClient.Width, parentClient.Height, BoundsSpecified.All);
            BeginInvoke(new Action(PinEmbeddedWindowToParentOrigin));
        }

        private void PinEmbeddedWindowToParentOrigin()
        {
            if (_embeddedParentHandle == IntPtr.Zero || GetParent(Handle) != _embeddedParentHandle) return;

            // Shown 처리 뒤 WinForms가 이전 독립 창 좌표를 복원하는 경우를 한 번 더 부모 원점으로 교정한다.
            SetBounds(0, 0, Width, Height, BoundsSpecified.Location | BoundsSpecified.Size);
            MoveWindow(Handle, 0, 0, Width, Height, true);
        }

        private void EnsureInputHook()
        {
            if (_inputHook != null)
                return;

            // 본창 표시가 WebView 로딩 메시지에 막혀도 단축키 기능은 먼저 살아 있어야 한다.
            var inputHook = new InputHookManager();
            if (!inputHook.IsInstalled)
            {
                // 첫 등록이 실패하면 Shown 시점의 EnsureInputHook 호출이 한 번 더 시도할 수 있도록 null을 유지한다.
                Logger.Log($"전역 입력 훅 등록 실패: {inputHook.InstallationError}");
                inputHook.Dispose();
                return;
            }

            _inputHook = inputHook;
            inputHook.TryRouteEditorKeyboardInput = TryRouteEditorKeyboardInput;
            inputHook.OnInputEvent += (hookSender, inputArgs) =>
            {
                if (IsDisposed) return;
                BeginInvoke(new Action(() => HandleHookInput(inputArgs)));
            };
        }
        private void LoadDatabase(string path = "database.json")
        {
            if (!File.Exists(path))
            {
                MessageBox.Show($"{path} 파일을 찾을 수 없습니다.\n프로그램을 종료합니다.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.OnFormClosed(null!);
                return;
            }

            try
            {
                var json = File.ReadAllText(path);
                var root = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<JsonElement>>>>(json);

                if (root != null)
                {
                    foreach (var type in root)
                    {
                        foreach (var subCat in type.Value)
                        {
                            string subCategoryName = subCat.Key;

                            foreach (var item in subCat.Value)
                            {
                                string name = item.GetProperty("Name").GetString() ?? "";

                                if (type.Key == "스트라타젬" && item.TryGetProperty("Sequence", out JsonElement seqElement))
                                {
                                    _sequenceMap[name] = seqElement.EnumerateArray()
                                        .Select(x => x.GetString()?.ToLowerInvariant() ?? "")
                                        .ToArray();
                                }

                                _parsedData.Add((type.Key, subCategoryName, name));
                            }
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                MessageBox.Show($"JSON 파일 문법 오류:\n{ex.Message}\n프로그램을 종료합니다.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.OnFormClosed(null!);
            }
        }

        private void LoadUserSetting()
        {
            List<InputConfigCandidate> configCandidates = new();

            string localSavesPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Arrowhead\Helldivers2\saves"
            );

            if (Directory.Exists(localSavesPath))
            {
                var dir = new DirectoryInfo(localSavesPath);
                foreach (FileInfo file in dir.GetFiles("*_input_settings.config"))
                {
                    // 현재 Windows 사용자 저장본은 현재 Steam 계정 후보와 같은 우선순위로 검사한다.
                    configCandidates.Add(new InputConfigCandidate { File = file, Priority = 300, Source = "로컬 저장" });
                }
            }

            using (var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                string? steamPath = steamKey?.GetValue("SteamPath") as string;
                if (!string.IsNullOrEmpty(steamPath))
                {
                    string loginUsersPath = Path.Combine(steamPath, "config", "loginusers.vdf");
                    if (File.Exists(loginUsersPath))
                    {
                        string vdf = File.ReadAllText(loginUsersPath);
                        var userMatches = Regex.Matches(vdf, "\"(\\d{17})\"\\s*\\{([\\s\\S]*?)\\}", RegexOptions.Singleline);
                        foreach (Match userMatch in userMatches)
                        {
                            string body = userMatch.Groups[2].Value;
                            bool isMostRecent = Regex.IsMatch(body, "\"MostRecent\"\\s*\"1\"");
                            bool isAutoLogin = Regex.IsMatch(body, "\"AutoLogin\"\\s*\"1\"");
                            Match timestampMatch = Regex.Match(body, "\"Timestamp\"\\s*\"(\\d+)\"");
                            long.TryParse(timestampMatch.Groups[1].Value, out long timestamp);

                            if (!long.TryParse(userMatch.Groups[1].Value, out long id64) || id64 < 76561197960265728)
                                continue;

                            string steamID3 = (id64 - 76561197960265728).ToString(CultureInfo.InvariantCulture);
                            string steamCloudPath = Path.Combine(steamPath, "userdata", steamID3, "553850", "remote", "input_settings.config");

                            if (File.Exists(steamCloudPath))
                            {
                                // 계정을 모두 후보에 넣되 MostRecent, AutoLogin, Timestamp 순으로 현재 계정을 우선한다.
                                int priority = isMostRecent ? 300 : isAutoLogin ? 250 : timestamp > 0 ? 200 : 100;
                                configCandidates.Add(new InputConfigCandidate
                                {
                                    File = new FileInfo(steamCloudPath),
                                    Priority = priority,
                                    Source = isMostRecent ? "Steam MostRecent" : isAutoLogin ? "Steam AutoLogin" : "Steam 계정",
                                    AccountTimestamp = timestamp
                                });
                            }
                        }
                    }
                }
            }

            // 같은 파일이 중복 발견되면 높은 계정 우선순위를 유지하고, 파싱 실패 시 다음 후보를 계속 시도한다.
            var orderedCandidates = configCandidates
                .Where(candidate => candidate.File.Exists && candidate.File.Length > 0)
                .GroupBy(candidate => candidate.File.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(candidate => candidate.Priority).First())
                .OrderByDescending(candidate => candidate.Priority)
                .ThenByDescending(candidate => candidate.AccountTimestamp)
                .ThenByDescending(candidate => candidate.File.LastWriteTimeUtc)
                .ToList();

            foreach (InputConfigCandidate candidate in orderedCandidates)
            {
                if (TryApplyUserSetting(candidate.File))
                {
                    Logger.Log($"게임 입력 설정 불러오기 성공: {candidate.Source}, {candidate.File.FullName}");
                    return;
                }

                Logger.Log($"게임 입력 설정 파싱 실패, 다음 후보 시도: {candidate.Source}, {candidate.File.FullName}");
            }

            Logger.Log("게임 입력 설정을 불러오지 못해 기본값 또는 수동 설정을 사용합니다.");
        }

        private bool TryApplyUserSetting(FileInfo configFile)
        {
            string rawConfig;
            try
            {
                rawConfig = File.ReadAllText(configFile.FullName);
            }
            catch (Exception ex)
            {
                Logger.Log($"게임 입력 설정 읽기 실패: {configFile.FullName}, {ex.Message}");
                return false;
            }

            int avatarIdx = rawConfig.IndexOf("Avatar = {");
            string avatarBlock = (avatarIdx != -1) ? rawConfig.Substring(avatarIdx) : "";

            int playerIdx = rawConfig.IndexOf("Player = {");
            string playerBlock = (playerIdx != -1) ? rawConfig.Substring(playerIdx) : "";

            int stratagemIdx = rawConfig.IndexOf("Stratagem = {");
            string stratagemBlock = (stratagemIdx != -1) ? rawConfig.Substring(stratagemIdx) : "";

            var keys = new[] { "Fire", "Aim", "OpenChat", "Start", "Up", "Left", "Down", "Right" };
            var parsedStratagemKeys = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            var parsedMouseKeys = new Dictionary<uint, (string Trigger, float Threshold)>
            {
                [(uint)Keys.LButton] = ("Hold", 0),
                [(uint)Keys.RButton] = ("Hold", 0)
            };
            uint? parsedChatKey = null;
            string? parsedStratagemType = null;

            foreach (var k in keys)
            {
                string targetBlock = (k == "Fire" || k == "Aim") ? avatarBlock : (k == "OpenChat") ? playerBlock : stratagemBlock;

                if (string.IsNullOrEmpty(targetBlock) || !targetBlock.Contains($"{k} ="))
                {
                    continue;
                }

                var keyBlockMatch = Regex.Match(targetBlock, k + @"\s*=\s*\[(.*?)\]", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (!keyBlockMatch.Success) continue;

                var matches = Regex.Matches(keyBlockMatch.Groups[1].Value, @"\{([\s\S]*?)\}", RegexOptions.Singleline);

                foreach (Match braceMatch in matches)
                {
                    string inner = braceMatch.Groups[1].Value;

                    var triggerMatch = Regex.Match(inner, @"trigger\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
                    var deviceMatch = Regex.Match(inner, @"device_type\s*=\s*""(Keyboard|Mouse)""", RegexOptions.IgnoreCase);
                    var inputMatch = Regex.Match(inner, @"input\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
                    var thresholdMatch = Regex.Match(inner, @"threshold\s*=\s*([0-9.]+)", RegexOptions.IgnoreCase);

                    if (!deviceMatch.Success || !inputMatch.Success || !triggerMatch.Success)
                        continue;

                    string key = k.ToLower();
                    string device = deviceMatch.Groups[1].Value.ToLower();
                    string input = inputMatch.Groups[1].Value.ToLower();
                    string trigger = triggerMatch.Groups[1].Value;
                    float threshold = 0;
                    if (thresholdMatch.Success)
                        float.TryParse(thresholdMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold);
                    threshold *= 1000f;
                    var vk = ParseInputKey(input);

                    if (key == "fire" || key == "aim")
                    {
                        if (device == "mouse" && input.Contains("mousebutton") && vk.HasValue)
                        {
                            parsedMouseKeys[vk.Value] = (trigger, threshold);
                        }
                        continue;
                    }

                    if (vk.HasValue)
                    {
                        if (key == "openchat") parsedChatKey = vk.Value;
                        else parsedStratagemKeys[key] = vk.Value;
                    }

                    if (key == "start")
                        parsedStratagemType = trigger;

                    break;
                }
            }

            string[] requiredStratagemKeys = { "start", "up", "down", "left", "right" };
            if (requiredStratagemKeys.Any(key => !parsedStratagemKeys.ContainsKey(key)))
                return false;

            // 필수 키를 모두 찾은 후보만 한 번에 반영해 실패한 파일의 일부 값이 섞이지 않게 한다.
            foreach (var pair in parsedStratagemKeys)
                _stratagemKey[pair.Key] = pair.Value;

            _mouseKey.Clear();
            foreach (var pair in parsedMouseKeys)
                _mouseKey[pair.Key] = pair.Value;

            if (parsedChatKey.HasValue)
                _chatKey = parsedChatKey.Value;
            if (!string.IsNullOrWhiteSpace(parsedStratagemType))
                _stratagemType = parsedStratagemType;

            return true;
        }
        private async void InitializeWebView()
        {
            try
            {
                await InitializeWebViewControl(_webView!);
            }
            catch
            {
                MessageBox.Show(
                    "WebView2 런타임이 없거나 초기화에 실패했습니다.\n\n" +
                    "확인을 누르면 WebView2 런타임 설치 파일을 다운로드합니다.\n" +
                    "다운로드한 설치 파일은 관리자 권한으로 실행해 주세요.",
                    "오류",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                    UseShellExecute = true
                });

                this.OnFormClosed(null!);
            }
        }

        private async Task InitializeEditorWebView(WebView2 webView)
        {
            try
            {
                await InitializeWebViewControl(webView);
            }
            catch
            {
                MessageBox.Show(
                    "프리셋 편집창 WebView2 초기화에 실패했습니다.",
                    "오류",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private async Task InitializeWebViewControl(WebView2 webView)
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userDataFolder = Path.Combine(localAppData, "HD2_Helper_Cache");
            string exeFolder = AppDomain.CurrentDomain.BaseDirectory;

            var options = new CoreWebView2EnvironmentOptions("--disable-gpu --disable-gpu-compositing");
            var env = await WithTimeout(
                CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder, options: options),
                TimeSpan.FromSeconds(10)
            );

            await WithTimeout(
                webView.EnsureCoreWebView2Async(env),
                TimeSpan.FromSeconds(10));

            RegisterWebView(webView);

            var settings = webView.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;

            webView.CoreWebView2.SetVirtualHostNameToFolderMapping("hd2.local", exeFolder, CoreWebView2HostResourceAccessKind.Allow);
            webView.CoreWebView2.WebMessageReceived += (_, e) => HandleWebJsonMessage(e.WebMessageAsJson, webView);
            webView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                // 보조 편집창까지 같은 HTML을 쓰므로, 로드가 끝난 WebView에 현재 앱 상태를 바로 주입한다.
                SendDisabledItemsToWeb(webView);
                SendPresetsToWeb(webView);
                SendSettingsToWeb(webView);
                // F3을 나중에 열어도 원본 창에서 아직 수동 저장하지 않은 현재 로드아웃을 마지막으로 덮어써 동일하게 보이게 한다.
                SendCurrentLoadoutToWeb(webView);
            };

            var assembly = System.Reflection.Assembly.GetEntryAssembly()!;
            using var stream = assembly.GetManifestResourceStream(Array.Find(assembly.GetManifestResourceNames(), n => n.EndsWith("index.html"))!)!;
            string htmlContent = new StreamReader(stream).ReadToEnd();

            webView.CoreWebView2.NavigateToString(htmlContent);
        }

        private void RegisterWebView(WebView2 webView)
        {
            if (!_webViews.Contains(webView))
                _webViews.Add(webView);

            webView.Disposed += (_, _) => _webViews.Remove(webView);
        }

        private IEnumerable<WebView2> GetReadyWebViews(WebView2? target = null)
        {
            var targets = target != null ? new[] { target } : _webViews.ToArray();
            foreach (var webView in targets)
            {
                if (!webView.IsDisposed && webView.CoreWebView2 != null)
                    yield return webView;
            }
        }

        private void PostWebMessageToViews(object payload, WebView2? target = null)
        {
            string json = JsonSerializer.Serialize(payload);
            foreach (var webView in GetReadyWebViews(target))
                webView.CoreWebView2.PostWebMessageAsJson(json);
        }

        private static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout)
        {
            Task delayTask = Task.Delay(timeout);
            Task completedTask = await Task.WhenAny(task, delayTask);

            if (completedTask == delayTask)
                throw new TimeoutException();

            return await task;
        }

        private static async Task WithTimeout(Task task, TimeSpan timeout)
        {
            Task delayTask = Task.Delay(timeout);
            Task completedTask = await Task.WhenAny(task, delayTask);

            if (completedTask == delayTask)
                throw new TimeoutException();

            await task;
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            HandleWebJsonMessage(e.WebMessageAsJson, _webView);
        }

        private void HandleWebJsonMessage(string json, WebView2? sourceWebView = null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("type", out var typeElement)) return;

                string? type = typeElement.GetString();
                if (type == "LOAD_DISABLED_ITEMS")
                {
                    SendDisabledItemsToWeb(sourceWebView);
                }
                else if (type == "LOAD_PRESETS")
                {
                    SendPresetsToWeb(sourceWebView);
                }
                else if (type == "LOAD_SETTINGS")
                {
                    SendSettingsToWeb(sourceWebView);
                }
                else if (type == "IMAGE_CACHE_READY")
                {
                    // 별도 편집창이 준비될 때 원본 창이 앞으로 튀어나오지 않도록 메인 WebView 신호만 처리한다.
                    if (ReferenceEquals(sourceWebView, _webView))
                    {
                        this.Show();
                        this.Activate();
                    }
                }
                else if (type == "SET_INPUT_DELAY")
                {
                    if (doc.RootElement.TryGetProperty("value", out var valueElement) && valueElement.TryGetInt32(out int value))
                    {
                        _inputDelay = Math.Clamp(value, 30, 100);
                        SaveSetting();
                        SendSettingsToWeb();
                    }
                }
                else if (type == "SET_ADDITIONAL_STRATAGEM_SLOTS")
                {
                    if (doc.RootElement.TryGetProperty("value", out var valueElement) && valueElement.TryGetInt32(out int value))
                    {
                        int normalizedValue = Math.Clamp(value, 0, MaxAdditionalStratagemSlots);
                        if (_additionalStratagemSlots != normalizedValue)
                        {
                            // UI, 프리셋, 휠 모두 같은 슬롯 수를 쓰도록 상태 배열을 먼저 확장하거나 축소한다.
                            _additionalStratagemSlots = normalizedValue;
                            ResizeStratagemSlotState();
                            AdjustStratagemSlotClientHeight(sourceWebView);
                            SaveSetting();
                        }

                        SendSettingsToWeb();
                    }
                }
                else if (type == "START_KEY_CAPTURE")
                {
                    if (doc.RootElement.TryGetProperty("target", out var targetElement))
                    {
                        string? target = targetElement.GetString();
                        if (IsValidSettingsKeyTarget(target))
                        {
                            _waitingKeyTarget = target;
                            _isWaitingForKey = true;
                            SendSettingsToWeb();
                        }
                    }
                }
                else if (type == "SET_STRATAGEM_LAYOUT")
                {
                    if (doc.RootElement.TryGetProperty("compact", out var compactElement))
                    {
                        _stratagemCompactLayout = compactElement.GetBoolean();
                        SaveSetting();
                        SendSettingsToWeb();
                    }
                }
                else if (type == "SET_LEGACY_EQUIPMENT_LAYOUT")
                {
                    if (doc.RootElement.TryGetProperty("enabled", out var enabledElement))
                    {
                        // 목록형 전환값은 장비 선택 창을 새로 열 때뿐 아니라 현재 열린 모든 WebView에도 즉시 반영한다.
                        _useLegacyEquipmentLayout = enabledElement.GetBoolean();
                        SaveSetting();
                        SendSettingsToWeb();
                    }
                }
                else if (type == "SET_STRATAGEM_RESELECT_ENABLED")
                {
                    if (doc.RootElement.TryGetProperty("enabled", out var enabledElement))
                    {
                        // 재선택 ON은 OCR/아이콘 확인으로 이미 장착된 슬롯도 교체하고, OFF는 빈 슬롯 좌표 선택만 수행한다.
                        _stratagemReselectEnabled = enabledElement.GetBoolean();
                        SaveSetting();
                        SendSettingsToWeb();
                    }
                }
                else if (type == "SET_PRESET_AUTOSAVE")
                {
                    if (doc.RootElement.TryGetProperty("enabled", out var enabledElement))
                    {
                        // 자동저장 여부는 프리셋 데이터가 아닌 프로그램 전체 설정으로 보관한다.
                        _presetAutoSaveEnabled = enabledElement.GetBoolean();
                        SaveSetting();
                        SendSettingsToWeb();
                    }
                }
                else if (type == "SET_TEST_MODE")
                {
                    if (doc.RootElement.TryGetProperty("enabled", out var enabledElement))
                    {
                        _testModeEnabled = enabledElement.GetBoolean();
                        SaveSetting();
                        SendSettingsToWeb();
                    }
                }
                else if (type == "SET_TIMER_TEST_OPTION")
                {
                    string timer = doc.RootElement.TryGetProperty("timer", out var timerElement)
                        ? timerElement.GetString() ?? ""
                        : "";
                    if (doc.RootElement.TryGetProperty("paused", out var pausedElement))
                    {
                        // 테스트 스위치는 ON일 때 해당 주기 작업만 일시정지하며 실제 기능 설정값은 바꾸지 않는다.
                        SetTimerPauseOption(timer, pausedElement.GetBoolean());
                        SaveSetting();
                        ApplyTimerPauseOptions();
                        SendSettingsToWeb();
                    }
                }
                else if (type == "SET_INACTIVE_GAME_AUDIO_MUTE")
                {
                    if (doc.RootElement.TryGetProperty("enabled", out var enabledElement))
                    {
                        _muteGameAudioWhenInactive = enabledElement.GetBoolean();
                        SaveSetting();
                        UpdateInactiveGameAudioMute();
                        SendSettingsToWeb();
                    }
                }
                else if (type == "SET_EXCLUDE_OVERLAYS_FROM_CAPTURE")
                {
                    if (doc.RootElement.TryGetProperty("enabled", out var enabledElement))
                    {
                        _excludeOverlaysFromCapture = enabledElement.GetBoolean();
                        SaveSetting();
                        ApplyCaptureExclusionToHelperWindows();
                        SendSettingsToWeb();
                    }
                }
                else if (type == "SET_AUTO_RELOAD_ENABLED")
                {
                    if (doc.RootElement.TryGetProperty("enabled", out var enabledElement))
                    {
                        // 자동 재장전은 사용자가 명시적으로 켠 경우에만 중앙 장전 안내 감지를 시작한다.
                        _autoReloadEnabled = enabledElement.GetBoolean();
                        if (_autoReloadDetectionTimer != null)
                        {
                            // 자동 재장전 검사는 상시 타이머가 아니라 실제 발사 입력 시점에만 수행한다.
                            _autoReloadDetectionTimer.Stop();
                        }
                        SaveSetting();
                        SendSettingsToWeb();
                    }
                }
                else if (type == "SET_SETTINGS_PANEL_COLLAPSED")
                {
                    if (doc.RootElement.TryGetProperty("collapsed", out var collapsedElement))
                        AdjustSettingsPanelClientWidth(sourceWebView, collapsedElement.GetBoolean());
                }
                else if (type == "CANCEL_KEY_CAPTURE")
                {
                    _isWaitingForKey = false;
                    _waitingKeyTarget = null;
                    SendSettingsToWeb();
                }
                else if (type == "CLEAR_KEY_SETTING")
                {
                    if (doc.RootElement.TryGetProperty("target", out var targetElement))
                    {
                        // 설정 행 우클릭은 캡처 대기 없이 해당 키를 바로 비운다.
                        ClearSettingsKey(targetElement.GetString());
                    }
                }
                else if (type == "SAVE_DISABLED_ITEMS")
                {
                    var items = doc.RootElement.TryGetProperty("items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array
                        ? itemsElement.EnumerateArray()
                            .Select(item => item.GetString())
                            .Where(item => !string.IsNullOrWhiteSpace(item))
                            .Select(item => item!.Trim())
                            .Distinct()
                            .OrderBy(item => item)
                            .ToArray()
                        : Array.Empty<string>();

                    _disabledItems = items.ToHashSet();
                    SaveDisabledItems(items);
                    // 한쪽 창에서 제외 항목을 바꿔도 원본/편집창 목록이 즉시 같은 상태가 되도록 다시 배포한다.
                    SendDisabledItemsToWeb();
                }
                else if (type == "SAVE_PRESETS")
                {
                    if (doc.RootElement.TryGetProperty("presets", out var presetsElement) && presetsElement.ValueKind == JsonValueKind.Object)
                    {
                        SavePresets(presetsElement.GetRawText());
                        // 별도 편집창에서 저장한 프리셋도 원본 창에 곧바로 반영한다.
                        SendPresetsToWeb();
                    }
                }
                else if (type == "SET_SELECTED_PRESET")
                {
                    // WebView에서 직접 고른 선택만 settings.ini에 저장하고, 다른 창에는 단방향으로만 동기화한다.
                    string nextPresetId = doc.RootElement.TryGetProperty("id", out var idElement)
                        ? idElement.GetString() ?? ""
                        : "";

                    if (!string.Equals(_selectedPresetId, nextPresetId, StringComparison.Ordinal))
                    {
                        _selectedPresetId = nextPresetId;
                        SaveSetting();
                        SendSettingsToWeb();
                        SendPresetSelectionToWeb(_selectedPresetId);
                    }
                }
                else if (type == "SET_SELECTED_EQUIPMENT_PRESET")
                {
                    string nextPresetId = doc.RootElement.TryGetProperty("id", out var idElement)
                        ? idElement.GetString() ?? ""
                        : "";

                    if (!string.Equals(_selectedEquipmentPresetId, nextPresetId, StringComparison.Ordinal))
                    {
                        _selectedEquipmentPresetId = nextPresetId;
                        SaveSetting();
                        SendSettingsToWeb();
                        SendEquipmentPresetSelectionToWeb(_selectedEquipmentPresetId);
                    }
                }
                else if (type == "SET_CROSSHAIR_SETTINGS")
                {
                    if (doc.RootElement.TryGetProperty("settings", out var settingsElement))
                    {
                        var next = JsonSerializer.Deserialize<CrosshairSettings>(
                            settingsElement.GetRawText(),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                        );

                        if (next != null)
                        {
                            // WebView 편집값은 즉시 오버레이와 디스크 저장값에 반영한다.
                            _crosshairSettings = next.Normalized();
                            SaveCrosshairSettings();
                            UpdateCrosshairOverlay();
                            SendSettingsToWeb();
                        }
                    }
                }
                else if (type == "SET_SUPPORT_WEAPON_SETTINGS")
                {
                    if (doc.RootElement.TryGetProperty("settings", out var settingsElement))
                    {
                        var next = JsonSerializer.Deserialize<SupportWeaponAssistSettings>(
                            settingsElement.GetRawText(),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                        );

                        if (next != null)
                        {
                            var previous = _supportWeaponSettings.Normalized();
                            // 지원무기 보조 옵션은 게이지 오버레이와 경고음 계산에 즉시 반영한다.
                            _supportWeaponSettings = next.Normalized();
                            _supportWeaponMode = _supportWeaponSettings.Mode;
                            if (previous.WarningVolume != _supportWeaponSettings.WarningVolume
                                || !string.Equals(previous.WarningSoundPath, _supportWeaponSettings.WarningSoundPath, StringComparison.Ordinal))
                            {
                                SaveSupportWeaponSettings();
                            }
                            UpdateSupportWeaponGaugeOverlay();
                            RefreshSupportWeaponGaugeTimerState();
                            // F3에서 바꾼 지원무기 보조값도 원본 창의 현재 프리셋 표시와 즉시 맞춘다.
                            SendSettingsToWeb();
                        }
                    }
                }
                else if (type == "OPEN_SUPPORT_WARNING_SOUND_FILE")
                {
                    OpenSupportWarningSoundFileDialog();
                }
                else if (type == "PREVIEW_SUPPORT_WARNING_SOUND")
                {
                    PlaySupportWeaponWarningBeep(_supportWeaponSettings.WarningVolume, _supportWeaponSettings.WarningSoundPath);
                }
                else if (type == "CLEAR_SUPPORT_WARNING_SOUND_FILE")
                {
                    _supportWeaponSettings = _supportWeaponSettings.Normalized();
                    _supportWeaponSettings.WarningSoundPath = "";
                    SaveSupportWeaponSettings();
                    SendSettingsToWeb();
                }
                else if (type == "OPEN_CROSSHAIR_EDITOR")
                {
                    OpenCrosshairEditor();
                }
                else if (type == "OPEN_OCR_REGION_SETTINGS")
                {
                    OpenOcrRegionSettings();
                }
                else if (type == "OPEN_AUTO_RELOAD_CALIBRATION")
                {
                    OpenAutoReloadCalibration();
                }
                else if (type == "OPEN_PRESET_OVERLAY")
                {
                    ShowPresetOverlay();
                }
                else if (type == "CURRENT_LOADOUT")
                {
                    UpdateCurrentLoadoutFromWeb(doc.RootElement);
                }
            }
            catch { }
        }

        private void UpdateCurrentLoadoutFromWeb(JsonElement root)
        {
            if (!root.TryGetProperty("loadout", out var loadoutElement) || loadoutElement.ValueKind != JsonValueKind.Object)
                return;

            var slots = new string?[StratagemSlotCount];
            if (loadoutElement.TryGetProperty("stratagems", out var stratagemsElement) && stratagemsElement.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (var item in stratagemsElement.EnumerateArray())
                {
                    if (index >= slots.Length) break;

                    string? name = item.GetString();
                    slots[index++] = name;

                    if (!string.IsNullOrEmpty(name))
                    {
                        GetStratagemImage(name);
                    }
                }
            }

            if (loadoutElement.TryGetProperty("overlaySlots", out var overlaySlotsElement))
                _overlaySlotVisibility = ReadOverlaySlotVisibility(overlaySlotsElement);

            // 프리셋을 바꿔도 게임 안에는 직전 스트라타젬이 남아 있으므로 자동 선택의 시작 커서 보정값으로 보관한다.
            if (!AreSameSlots(_currentSlots, slots))
                _previousStratagemSlots = _currentSlots.ToArray();

            _currentSlots = slots;
            _currentLoadoutSlots = new[]
            {
                GetLoadoutString(loadoutElement, "armor"),
                GetLoadoutString(loadoutElement, "primary"),
                GetLoadoutString(loadoutElement, "secondary"),
                GetLoadoutString(loadoutElement, "grenade")
            };

            // 한 창에서 바꾼 슬롯/장비는 저장하지 않아도 원본 창과 F3 편집창에 즉시 같은 모습으로 전달한다.
            SendCurrentLoadoutToWeb();
        }

        private void SendCurrentLoadoutToWeb(WebView2? target = null)
        {
            var payload = new
            {
                type = "CURRENT_LOADOUT_SYNC",
                loadout = new
                {
                    // 실행 중 편집값이 있을 때만 WebView의 마지막 프리셋 복원을 건너뛴다.
                    hasRuntimeLoadout = _currentSlots.Any(value => !string.IsNullOrWhiteSpace(value))
                        || _currentLoadoutSlots.Any(value => !string.IsNullOrWhiteSpace(value)),
                    stratagems = _currentSlots.Select(value => value ?? "").ToArray(),
                    overlaySlots = _overlaySlotVisibility.ToArray(),
                    armor = _currentLoadoutSlots.ElementAtOrDefault(0) ?? "",
                    primary = _currentLoadoutSlots.ElementAtOrDefault(1) ?? "",
                    secondary = _currentLoadoutSlots.ElementAtOrDefault(2) ?? "",
                    grenade = _currentLoadoutSlots.ElementAtOrDefault(3) ?? ""
                }
            };

            PostWebMessageToViews(payload, target);
        }

        private static bool AreSameSlots(string?[] left, string?[] right)
        {
            int max = Math.Max(left.Length, right.Length);
            for (int i = 0; i < max; i++)
            {
                string? leftValue = i < left.Length ? left[i] : null;
                string? rightValue = i < right.Length ? right[i] : null;
                if (!string.Equals(leftValue, rightValue, StringComparison.Ordinal))
                    return false;
            }

            return true;
        }

        private static bool[] CreateDefaultOverlaySlotVisibility()
        {
            // 기본 휠은 첫 줄의 1~5번만 표시하며, 추가 보관 슬롯도 처음에는 숨긴다.
            return Enumerable.Range(0, StratagemSlotCount).Select(index => index < 5).ToArray();
        }

        private static void ResizeStratagemSlotState()
        {
            int slotCount = StratagemSlotCount;
            string?[] ResizeNames(string?[] source) => Enumerable.Range(0, slotCount)
                .Select(index => index < source.Length ? source[index] : null)
                .ToArray();

            // 슬롯 수 변경은 기존 선택과 이전 선택 상태를 앞쪽부터 보존하고, 새 슬롯만 빈 값으로 만든다.
            _currentSlots = ResizeNames(_currentSlots);
            _previousStratagemSlots = ResizeNames(_previousStratagemSlots);

            bool[] defaults = CreateDefaultOverlaySlotVisibility();
            _overlaySlotVisibility = defaults
                .Select((fallback, index) => index < _overlaySlotVisibility.Length ? _overlaySlotVisibility[index] : fallback)
                .ToArray();

            // 사라진 슬롯의 단축키는 다시 슬롯을 늘렸을 때 의도치 않게 되살아나지 않도록 정리한다.
            foreach (int slotIndex in _slotKey.Keys.Where(index => index >= slotCount).ToList())
                _slotKey.Remove(slotIndex);
        }

        private static bool[] ReadOverlaySlotVisibility(JsonElement element)
        {
            var visibility = CreateDefaultOverlaySlotVisibility();
            if (element.ValueKind != JsonValueKind.Array)
                return visibility;

            int index = 0;
            foreach (var value in element.EnumerateArray())
            {
                if (index >= visibility.Length) break;
                if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                    visibility[index] = value.GetBoolean();
                index++;
            }

            return visibility;
        }

        private static string?[] GetStratagemStartSlots()
        {
            // 게임 장착 상태를 직접 읽을 수 없으므로, 프리셋 변경 직전 UI 상태만 기존 장착 추정값으로 사용한다.
            return _previousStratagemSlots.ToArray();
        }

        private static string? GetLoadoutString(JsonElement loadoutElement, string propertyName)
        {
            return loadoutElement.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        }


        private static string[] LoadDisabledItems()
        {
            if (!File.Exists(DisabledItemsPath))
            {
                _disabledItems.Clear();
                return Array.Empty<string>();
            }

            var items = File.ReadAllLines(DisabledItemsPath, Encoding.UTF8)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith(";") && !line.StartsWith("#"))
                .Distinct()
                .ToArray();

            _disabledItems = items.ToHashSet(StringComparer.Ordinal);
            return items;
        }

        private static void SaveDisabledItems(IEnumerable<string> items)
        {
            Directory.CreateDirectory(AppDataPath);
            File.WriteAllLines(DisabledItemsPath, items, Encoding.UTF8);
        }

        private void SendDisabledItemsToWeb(WebView2? target = null)
        {
            var payload = new
            {
                type = "DISABLED_ITEMS_LOADED",
                items = LoadDisabledItems()
            };

            PostWebMessageToViews(payload, target);
        }

        private static string LoadPresetsJson()
        {
            if (!File.Exists(PresetsPath)) return "{\"stratagemPresets\":[],\"equipmentPresets\":[]}";

            string json = File.ReadAllText(PresetsPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return "{\"stratagemPresets\":[],\"equipmentPresets\":[]}";

            using var doc = JsonDocument.Parse(json);
            // 이전 배열 형식은 읽기 호환성을 유지하고, 새 저장부터 두 컬렉션 객체 형식으로 기록한다.
            return doc.RootElement.ValueKind is JsonValueKind.Array or JsonValueKind.Object
                ? json
                : "{\"stratagemPresets\":[],\"equipmentPresets\":[]}";
        }

        private static void SavePresets(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            if (!doc.RootElement.TryGetProperty("stratagemPresets", out var stratagemPresets)
                || stratagemPresets.ValueKind != JsonValueKind.Array
                || !doc.RootElement.TryGetProperty("equipmentPresets", out var equipmentPresets)
                || equipmentPresets.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            Directory.CreateDirectory(AppDataPath);

            string existingJson = File.Exists(PresetsPath)
                ? File.ReadAllText(PresetsPath, Encoding.UTF8)
                : "[]";

            if (ShouldRejectSuspiciousPresetOverwrite(existingJson, stratagemPresets))
            {
                SaveRejectedPresets(json);
                return;
            }

            BackupPresetsBeforeOverwrite(existingJson);
            File.WriteAllText(PresetsPath, json, Encoding.UTF8);
        }

        private static void SavePresetEquipmentLink(string stratagemPresetId, string equipmentPresetId)
        {
            if (string.IsNullOrWhiteSpace(stratagemPresetId)) return;

            try
            {
                var root = JsonNode.Parse(LoadPresetsJson()) as JsonObject;
                var stratagemPresets = root?["stratagemPresets"] as JsonArray;
                if (stratagemPresets == null) return;

                foreach (var node in stratagemPresets.OfType<JsonObject>())
                {
                    if (!string.Equals(node["id"]?.GetValue<string>(), stratagemPresetId, StringComparison.Ordinal))
                        continue;

                    // F4 장비 선택도 자동저장 ON이면 현재 스트라타젬 프리셋의 하위 장비 선택으로 기록한다.
                    node["equipmentPresetId"] = equipmentPresetId ?? "";
                    SavePresets(root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    return;
                }
            }
            catch
            {
                // 프리셋 파일을 읽을 수 없는 상태에서는 기존 선택 동작만 유지하고 저장은 건너뛴다.
            }
        }

        private static bool ShouldRejectSuspiciousPresetOverwrite(string existingJson, JsonElement incomingPresets)
        {
            try
            {
                using var existingDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(existingJson) ? "[]" : existingJson);
                JsonElement existingPresets = GetStratagemPresetsElement(existingDoc.RootElement);
                if (existingPresets.ValueKind != JsonValueKind.Array)
                    return false;

                int existingCount = existingPresets.GetArrayLength();
                int incomingCount = incomingPresets.GetArrayLength();
                if (existingCount <= 1 || incomingCount != 1)
                    return false;

                var incoming = incomingPresets[0];
                string incomingName = incoming.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";

                // 로드 실패처럼 보이는 상태에서 빈 기본 프리셋 하나가 기존 여러 프리셋을 덮어쓰는 상황을 막는다.
                return incomingName.StartsWith("프리셋 ", StringComparison.Ordinal)
                    && IsPresetLoadoutEmpty(incoming.TryGetProperty("loadout", out var loadoutElement) ? loadoutElement : default);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPresetLoadoutEmpty(JsonElement loadout)
        {
            if (loadout.ValueKind != JsonValueKind.Object)
                return true;

            if (loadout.TryGetProperty("stratagems", out var stratagemsElement) && stratagemsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in stratagemsElement.EnumerateArray())
                {
                    if (!string.IsNullOrWhiteSpace(item.GetString()))
                        return false;
                }
            }

            foreach (string property in new[] { "armor", "primary", "secondary", "grenade" })
            {
                if (loadout.TryGetProperty(property, out var propertyElement) && !string.IsNullOrWhiteSpace(propertyElement.GetString()))
                    return false;
            }

            return true;
        }

        private static void BackupPresetsBeforeOverwrite(string existingJson)
        {
            if (string.IsNullOrWhiteSpace(existingJson) || existingJson.Trim() == "[]")
                return;

            Directory.CreateDirectory(PresetBackupPath);

            string backupPath = Path.Combine(PresetBackupPath, $"presets-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
            File.WriteAllText(backupPath, existingJson, Encoding.UTF8);
            CleanupOldPresetBackups();
        }

        private static void SaveRejectedPresets(string rejectedJson)
        {
            Directory.CreateDirectory(PresetBackupPath);

            // 차단된 저장 요청도 남겨두면, 이후 원인 분석 시 어떤 데이터가 덮어쓰려 했는지 확인할 수 있다.
            string rejectedPath = Path.Combine(PresetBackupPath, $"rejected-presets-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
            File.WriteAllText(rejectedPath, rejectedJson, Encoding.UTF8);
        }

        private static void CleanupOldPresetBackups()
        {
            var oldBackups = Directory.GetFiles(PresetBackupPath, "presets-*.json")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(50);

            foreach (var file in oldBackups)
            {
                try { file.Delete(); }
                catch { }
            }
        }

        private static Dictionary<string, OcrRegionSettings> CreateDefaultOcrRegionSettings()
        {
            // OCR 항목별 기본 캡처 영역은 1920x1080 기준 게임 UI 좌표로 관리한다.
            string[] types = { "스트라타젬", "주 무기", "보조 무기", "방어구", "투척 무기" };
            return types.ToDictionary(type => type, OcrRegionSettings.DefaultFor);
        }

        private static bool TryParseOcrRegionSettingKey(string key, out string? targetType, out string? property)
        {
            targetType = null;
            property = null;

            if (!key.StartsWith("ocr.", StringComparison.OrdinalIgnoreCase))
                return false;

            int propertySeparator = key.LastIndexOf('.');
            if (propertySeparator <= 4 || propertySeparator >= key.Length - 1)
                return false;

            targetType = key[4..propertySeparator];
            property = key[(propertySeparator + 1)..];
            return true;
        }

        private static void LoadSetting()
        {
            if (!File.Exists(SettingsPath)) return;

            bool loadedAutoReloadPromptRegion = false;
            foreach (string rawLine in File.ReadAllLines(SettingsPath, Encoding.UTF8))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;

                int separator = line.IndexOf('=');
                if (separator <= 0) continue;

                string key = line[..separator].Trim();
                string value = line[(separator + 1)..].Trim();

                if (key.Equals("inputDelay", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, out int delay)) _inputDelay = Math.Clamp(delay, 30, 100);
                }
                else if (key.Equals("additionalStratagemSlots", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, out int slotCount))
                        _additionalStratagemSlots = Math.Clamp(slotCount, 0, MaxAdditionalStratagemSlots);
                }
                else if (key.Equals("selectedPresetId", StringComparison.OrdinalIgnoreCase))
                {
                    // 선택 프리셋은 숫자 키 설정과 달리 문자열 id라서 그대로 읽는다.
                    _selectedPresetId = value;
                }
                else if (key.Equals("selectedEquipmentPresetId", StringComparison.OrdinalIgnoreCase))
                {
                    _selectedEquipmentPresetId = value;
                }
                else if (key.Equals("stratagemCompactLayout", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    _stratagemCompactLayout = vk != 0;
                }
                else if (key.Equals("useLegacyEquipmentLayout", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    _useLegacyEquipmentLayout = vk != 0;
                }
                else if (key.Equals("stratagemReselectEnabled", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    _stratagemReselectEnabled = vk != 0;
                }
                else if (key.Equals("presetAutoSaveEnabled", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    _presetAutoSaveEnabled = vk != 0;
                }
                else if (key.Equals("testModeEnabled", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    _testModeEnabled = vk != 0;
                }
                else if (key.Equals("pauseCrosshairTimer", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    _pauseCrosshairTimer = vk != 0;
                }
                else if (key.Equals("pauseSupportWeaponTimer", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    _pauseSupportWeaponTimer = vk != 0;
                }
                else if (key.Equals("pauseSoftwareCursorTimer", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    _pauseSoftwareCursorTimer = vk != 0;
                }
                else if (key.Equals("pauseAudioMuteTimer", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    _pauseAudioMuteTimer = vk != 0;
                }
                else if (key.Equals("pauseGamepadLoop", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    _pauseGamepadLoop = vk != 0;
                }
                else if (key.Equals("muteGameAudioWhenInactive", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    _muteGameAudioWhenInactive = vk != 0;
                }
                else if (key.Equals("excludeOverlaysFromCapture", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    _excludeOverlaysFromCapture = vk != 0;
                }
                else if (key.Equals("autoReloadEnabled", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    _autoReloadEnabled = vk != 0;
                }
                else if (key.Equals("autoReload.promptRegionVersion", StringComparison.OrdinalIgnoreCase))
                {
                    // 이전 좌하단 HUD 보정값은 중앙 장전 안내 영역과 호환되지 않아 한 번만 새 기본값으로 교체한다.
                    loadedAutoReloadPromptRegion = int.TryParse(value, out int version) && version >= 1;
                }
                else if (key.StartsWith("autoReload.", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value, out int autoReloadValue))
                {
                    // 중앙 장전 안내의 좌표는 다른 OCR 항목과 별개로 보관해 보정값이 서로 영향을 주지 않게 한다.
                    _autoReloadSettings = _autoReloadSettings.WithProperty(key["autoReload.".Length..], autoReloadValue).Normalized();
                }
                else if (TryGetManualStratagemDirection(key, out string manualDirection))
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    // 0은 수동 설정 해제로 취급해 게임 설정 파일 값으로 자동 복귀한다.
                    if (vk == 0) _manualStratagemKey.Remove(manualDirection);
                    else _manualStratagemKey[manualDirection] = vk;
                }
                else if (key.Equals("autoSelectKey", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    _autoSelectKey = vk;
                }
                else if (key.Equals("overlayKey", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    _overlayKey = vk;
                }
                else if (key.Equals("reinforceKey", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    _reinforceKey = vk;
                }
                else if (key.Equals("stratagemComboKey", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    _stratagemComboKey = vk;
                }
                else if (key.Equals("crosshairToggleKey", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    _crosshairToggleKey = vk;
                }
                else if (key.Equals("helperEditorKey", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    _helperEditorKey = vk;
                }
                else if (key.Equals("presetOverlayKey", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("ownedScanKey", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    _presetOverlayKey = vk;
                }
                else if (TryParseOcrRegionSettingKey(key, out string? ocrType, out string? property)
                    && ocrType != null
                    && property != null
                    && int.TryParse(value, out int ocrValue))
                {
                    if (!_ocrRegionSettings.TryGetValue(ocrType, out var settings))
                        settings = OcrRegionSettings.DefaultFor(ocrType);

                    // OCR 좌표 설정은 항목별로 나뉘어 저장되므로 읽는 즉시 해당 항목의 값만 갱신한다.
                    _ocrRegionSettings[ocrType] = settings.WithProperty(property, ocrValue).Normalized();
                }
                else if (key.StartsWith("slot", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(key[4..], out int slot)
                    && slot >= 1
                    && slot <= StratagemSlotCount)
                {
                    if (!uint.TryParse(value, out uint vk)) continue;
                    if (vk == 0) _slotKey.Remove(slot - 1);
                    else _slotKey[slot - 1] = vk;
                }
            }

            if (!loadedAutoReloadPromptRegion)
            {
                // 구버전의 좌하단 빨간 HUD 보정값은 현재 방식에서 의미가 없으므로 중앙 안내용 기본값을 사용한다.
                _autoReloadSettings = new AutoReloadSettings();
            }

            // settings.ini에서 추가 슬롯 수를 읽은 뒤에만 배열 길이를 맞춰, 기존 프리셋 데이터가 잘리지 않게 한다.
            ResizeStratagemSlotState();
        }

        private static void SaveSetting()
        {
            Directory.CreateDirectory(AppDataPath);

            var lines = new List<string>
            {
                $"inputDelay={Math.Clamp(_inputDelay, 30, 100)}",
                $"additionalStratagemSlots={_additionalStratagemSlots}",
                $"stratagemCompactLayout={(_stratagemCompactLayout ? 1 : 0)}",
                $"useLegacyEquipmentLayout={(_useLegacyEquipmentLayout ? 1 : 0)}",
                $"stratagemReselectEnabled={(_stratagemReselectEnabled ? 1 : 0)}",
                $"presetAutoSaveEnabled={(_presetAutoSaveEnabled ? 1 : 0)}",
                $"testModeEnabled={(_testModeEnabled ? 1 : 0)}",
                $"pauseCrosshairTimer={(_pauseCrosshairTimer ? 1 : 0)}",
                $"pauseSupportWeaponTimer={(_pauseSupportWeaponTimer ? 1 : 0)}",
                $"pauseSoftwareCursorTimer={(_pauseSoftwareCursorTimer ? 1 : 0)}",
                $"pauseAudioMuteTimer={(_pauseAudioMuteTimer ? 1 : 0)}",
                $"pauseGamepadLoop={(_pauseGamepadLoop ? 1 : 0)}",
                $"muteGameAudioWhenInactive={(_muteGameAudioWhenInactive ? 1 : 0)}",
                $"excludeOverlaysFromCapture={(_excludeOverlaysFromCapture ? 1 : 0)}",
                $"autoReloadEnabled={(_autoReloadEnabled ? 1 : 0)}",
                $"autoReload.promptRegionVersion=1",
                $"autoReload.x={_autoReloadSettings.Normalized().X}",
                $"autoReload.y={_autoReloadSettings.Normalized().Y}",
                $"autoReload.width={_autoReloadSettings.Normalized().Width}",
                $"autoReload.height={_autoReloadSettings.Normalized().Height}",
                $"autoReload.border={_autoReloadSettings.Normalized().BorderThickness}",
                $"autoReload.minimumPromptMatches={_autoReloadSettings.Normalized().MinimumPromptMatches}",
                $"manualStratagemStartKey={GetManualStratagemKey("start")}",
                $"manualStratagemUpKey={GetManualStratagemKey("up")}",
                $"manualStratagemDownKey={GetManualStratagemKey("down")}",
                $"manualStratagemLeftKey={GetManualStratagemKey("left")}",
                $"manualStratagemRightKey={GetManualStratagemKey("right")}",
                $"autoSelectKey={_autoSelectKey}",
                $"overlayKey={_overlayKey}",
                $"reinforceKey={_reinforceKey}",
                $"stratagemComboKey={_stratagemComboKey}",
                $"crosshairToggleKey={_crosshairToggleKey}",
                $"helperEditorKey={_helperEditorKey}",
                $"presetOverlayKey={_presetOverlayKey}",
                // 두 종류 프리셋의 선택을 따로 저장해 재실행 후에도 같은 조합을 복원한다.
                $"selectedPresetId={_selectedPresetId}",
                $"selectedEquipmentPresetId={_selectedEquipmentPresetId}"
            };

            foreach (var (type, settings) in _ocrRegionSettings.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                var normalized = settings.Normalized();
                lines.Add($"ocr.{type}.x={normalized.X}");
                lines.Add($"ocr.{type}.y={normalized.Y}");
                lines.Add($"ocr.{type}.width={normalized.Width}");
                lines.Add($"ocr.{type}.height={normalized.Height}");
                lines.Add($"ocr.{type}.border={normalized.BorderThickness}");
            }

            for (int slot = 1; slot <= StratagemSlotCount; slot++)
            {
                lines.Add($"slot{slot}={(_slotKey.TryGetValue(slot - 1, out uint key) ? key : 0)}");
            }

            File.WriteAllLines(SettingsPath, lines, Encoding.UTF8);
        }

        private static bool IsValidSettingsKeyTarget(string? target)
        {
            if (string.IsNullOrWhiteSpace(target)) return false;
            if (TryGetManualStratagemDirection(target, out _)) return true;
            if (target is "autoSelectKey" or "overlayKey" or "reinforceKey" or "stratagemComboKey" or "crosshairToggleKey" or "helperEditorKey" or "presetOverlayKey") return true;
            return target.StartsWith("slot", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(target[4..], out int slot)
                && slot >= 1
                && slot <= StratagemSlotCount;
        }

        private static bool TryGetManualStratagemDirection(string? settingKey, out string direction)
        {
            // settings.ini 키와 WebView 캡처 대상을 한 곳에서 해석해 저장/해제 동작이 어긋나지 않게 한다.
            direction = settingKey?.ToLowerInvariant() switch
            {
                "manualstratagemstartkey" => "start",
                "manualstratagemupkey" => "up",
                "manualstratagemdownkey" => "down",
                "manualstratagemleftkey" => "left",
                "manualstratagemrightkey" => "right",
                _ => ""
            };
            return direction.Length > 0;
        }

        private static uint GetManualStratagemKey(string direction)
        {
            return _manualStratagemKey.TryGetValue(direction, out uint vk) ? vk : 0;
        }

        private static bool TryGetEffectiveStratagemKey(string direction, out uint vk)
        {
            // 수동값이 있으면 우선하고, 없으면 시작할 때 읽은 Helldivers 2 입력 설정값을 사용한다.
            if (_manualStratagemKey.TryGetValue(direction, out vk) && vk != 0)
                return true;

            return _stratagemKey.TryGetValue(direction, out vk) && vk != 0;
        }

        private void ClearSettingsKey(string? target)
        {
            if (!IsValidSettingsKeyTarget(target)) return;

            if (_waitingKeyTarget == target)
            {
                _isWaitingForKey = false;
                _waitingKeyTarget = null;
            }

            if (TryGetManualStratagemDirection(target, out string manualDirection))
                _manualStratagemKey.Remove(manualDirection);
            else if (target == "autoSelectKey") _autoSelectKey = 0;
            else if (target == "overlayKey") _overlayKey = 0;
            else if (target == "reinforceKey") _reinforceKey = 0;
            else if (target == "stratagemComboKey") _stratagemComboKey = 0;
            else if (target == "crosshairToggleKey") _crosshairToggleKey = 0;
            else if (target == "helperEditorKey") _helperEditorKey = 0;
            else if (target == "presetOverlayKey") _presetOverlayKey = 0;
            else if (target!.StartsWith("slot", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(target[4..], out int slot)
                && slot >= 1
                && slot <= StratagemSlotCount)
            {
                _slotKey.Remove(slot - 1);
            }

            SaveSetting();
            SendSettingsToWeb();
        }
        private void AssignCapturedSettingsKey(uint vkCode)
        {
            if (vkCode == (uint)Keys.LButton)
                return;

            if (vkCode == (uint)Keys.RButton)
                vkCode = 0;

            string? target = _waitingKeyTarget;
            _isWaitingForKey = false;
            _waitingKeyTarget = null;

            if (!IsValidSettingsKeyTarget(target)) return;

            if (TryGetManualStratagemDirection(target, out string manualDirection))
            {
                // 방향키끼리는 같은 수동 키가 중복되지 않게 하되 일반 단축키 설정과는 독립적으로 유지한다.
                if (vkCode == 0)
                {
                    _manualStratagemKey.Remove(manualDirection);
                }
                else
                {
                    foreach (string direction in _manualStratagemKey
                        .Where(item => item.Value == vkCode && !item.Key.Equals(manualDirection, StringComparison.OrdinalIgnoreCase))
                        .Select(item => item.Key)
                        .ToList())
                    {
                        _manualStratagemKey.Remove(direction);
                    }
                    _manualStratagemKey[manualDirection] = vkCode;
                }

                SaveSetting();
                SendSettingsToWeb();
                return;
            }

            if (vkCode != 0)
            {
                if (_autoSelectKey == vkCode) _autoSelectKey = 0;
                if (_overlayKey == vkCode) _overlayKey = 0;
                if (_reinforceKey == vkCode) _reinforceKey = 0;
                if (_stratagemComboKey == vkCode) _stratagemComboKey = 0;
                if (_crosshairToggleKey == vkCode) _crosshairToggleKey = 0;
                if (_helperEditorKey == vkCode) _helperEditorKey = 0;
                if (_presetOverlayKey == vkCode) _presetOverlayKey = 0;

                var slotKeys = _slotKey.Keys.ToList();
                foreach (var key in slotKeys)
                {
                    if (_slotKey[key] == vkCode)
                    {
                        _slotKey.Remove(key);
                    }
                }
            }

            if (target == "autoSelectKey") _autoSelectKey = vkCode;
            else if (target == "overlayKey") _overlayKey = vkCode;
            else if (target == "reinforceKey") _reinforceKey = vkCode;
            else if (target == "stratagemComboKey") _stratagemComboKey = vkCode;
            else if (target == "crosshairToggleKey") _crosshairToggleKey = vkCode;
            else if (target == "helperEditorKey") _helperEditorKey = vkCode;
            else if (target == "presetOverlayKey") _presetOverlayKey = vkCode;
            else if (target!.StartsWith("slot", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(target[4..], out int slot)
                && slot >= 1
                && slot <= StratagemSlotCount)
            {
                if (vkCode == 0) _slotKey.Remove(slot - 1);
                else _slotKey[slot - 1] = vkCode;
            }

            SaveSetting();
            SendSettingsToWeb();
        }

        private void SendPresetsToWeb(WebView2? target = null)
        {
            using var presetsDoc = JsonDocument.Parse(LoadPresetsJson());
            var payload = new
            {
                type = "PRESETS_LOADED",
                presets = presetsDoc.RootElement.Clone(),
                // 설정 메시지와 프리셋 목록은 비동기로 도착한다. 목록에도 마지막 선택 ID를 실어 초기 탭 복원을 보장한다.
                selectedPresetId = _selectedPresetId,
                selectedEquipmentPresetId = _selectedEquipmentPresetId
            };

            PostWebMessageToViews(payload, target);
        }

        private void SendSettingsToWeb(WebView2? target = null)
        {
            var supportWeaponSettings = _supportWeaponSettings.Normalized();
            supportWeaponSettings.Mode = SupportWeaponAssistSettings.NormalizeMode(_supportWeaponMode);

            object BuildManualStratagemKeyInfo(string direction)
            {
                uint manualValue = GetManualStratagemKey(direction);
                _stratagemKey.TryGetValue(direction, out uint automaticValue);
                string automaticName = GetKeyName(automaticValue);
                return new
                {
                    value = manualValue,
                    // 수동값이 없을 때 현재 자동으로 적용될 게임 설정 키도 함께 표시한다.
                    name = manualValue == 0 ? $"자동 ({automaticName})" : GetKeyName(manualValue),
                    automaticValue,
                    automaticName
                };
            }

            var slotKeys = Enumerable.Range(1, StratagemSlotCount)
                .Select(slot => new
                {
                    slot,
                    target = $"slot{slot}",
                    key = _slotKey.TryGetValue(slot - 1, out uint value) ? value : 0,
                    name = GetKeyName(_slotKey.TryGetValue(slot - 1, out uint keyName) ? keyName : 0)
                })
                .ToArray();

            var payload = new
            {
                type = "SETTINGS_LOADED",
                inputDelay = Math.Clamp(_inputDelay, 30, 100),
                additionalStratagemSlots = _additionalStratagemSlots,
                stratagemCompactLayout = _stratagemCompactLayout,
                useLegacyEquipmentLayout = _useLegacyEquipmentLayout,
                stratagemReselectEnabled = _stratagemReselectEnabled,
                presetAutoSaveEnabled = _presetAutoSaveEnabled,
                testModeEnabled = _testModeEnabled,
                timerPauses = new
                {
                    crosshair = _pauseCrosshairTimer,
                    supportWeapon = _pauseSupportWeaponTimer,
                    softwareCursor = _pauseSoftwareCursorTimer,
                    audioMute = _pauseAudioMuteTimer,
                    gamepad = _pauseGamepadLoop
                },
                muteGameAudioWhenInactive = _muteGameAudioWhenInactive,
                excludeOverlaysFromCapture = _excludeOverlaysFromCapture,
                autoReloadEnabled = _autoReloadEnabled,
                selectedPresetId = _selectedPresetId,
                selectedEquipmentPresetId = _selectedEquipmentPresetId,
                crosshair = _crosshairSettings.Normalized(),
                supportWeapon = supportWeaponSettings.Normalized(),
                waitingTarget = _isWaitingForKey ? _waitingKeyTarget : null,
                keys = new
                {
                    autoSelectKey = new { value = _autoSelectKey, name = GetKeyName(_autoSelectKey) },
                    overlayKey = new { value = _overlayKey, name = GetKeyName(_overlayKey) },
                    reinforceKey = new { value = _reinforceKey, name = GetKeyName(_reinforceKey) },
                    stratagemComboKey = new { value = _stratagemComboKey, name = GetKeyName(_stratagemComboKey) },
                    manualStratagemStartKey = BuildManualStratagemKeyInfo("start"),
                    manualStratagemUpKey = BuildManualStratagemKeyInfo("up"),
                    manualStratagemDownKey = BuildManualStratagemKeyInfo("down"),
                    manualStratagemLeftKey = BuildManualStratagemKeyInfo("left"),
                    manualStratagemRightKey = BuildManualStratagemKeyInfo("right"),
                    crosshairToggleKey = new { value = _crosshairToggleKey, name = GetKeyName(_crosshairToggleKey) },
                    helperEditorKey = new { value = _helperEditorKey, name = GetKeyName(_helperEditorKey) },
                    presetOverlayKey = new { value = _presetOverlayKey, name = GetKeyName(_presetOverlayKey) },
                    slots = slotKeys
                }
            };

            PostWebMessageToViews(payload, target);
        }

        private static void LoadCrosshairSettings()
        {
            try
            {
                if (!File.Exists(CrosshairPath)) return;

                var settings = JsonSerializer.Deserialize<CrosshairSettings>(
                    File.ReadAllText(CrosshairPath, Encoding.UTF8),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );

                if (settings != null)
                    _crosshairSettings = settings.Normalized();
            }
            catch
            {
                _crosshairSettings = new CrosshairSettings();
            }
        }

        private static void SaveCrosshairSettings()
        {
            Directory.CreateDirectory(AppDataPath);
            File.WriteAllText(
                CrosshairPath,
                JsonSerializer.Serialize(_crosshairSettings.Normalized(), new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8
            );
        }
        private static void LoadSupportWeaponSettings()
        {
            try
            {
                if (!File.Exists(SupportWeaponPath)) return;

                var settings = JsonSerializer.Deserialize<SupportWeaponAssistSettings>(
                    File.ReadAllText(SupportWeaponPath, Encoding.UTF8),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );

                if (settings != null)
                    _supportWeaponSettings = settings.Normalized();

            }
            catch
            {
                _supportWeaponSettings = new SupportWeaponAssistSettings();
            }

        }

        private static JsonElement GetStratagemPresetsElement(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Array) return root;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("stratagemPresets", out var presets)
                && presets.ValueKind == JsonValueKind.Array
                ? presets
                : default;
        }

        private static JsonElement GetEquipmentPresetsElement(JsonElement root)
        {
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("equipmentPresets", out var presets)
                && presets.ValueKind == JsonValueKind.Array
                ? presets
                : default;
        }

        private static void SaveSupportWeaponSettings()
        {
            var globalWarningSettings = new SupportWeaponAssistSettings
            {
                // 경고음 설정만 전체 통합으로 저장하고, 프리셋별 게이지/모드 값은 프리셋 JSON에만 남긴다.
                WarningVolume = _supportWeaponSettings.WarningVolume,
                WarningSoundPath = _supportWeaponSettings.WarningSoundPath
            }.Normalized();

            Directory.CreateDirectory(AppDataPath);
            File.WriteAllText(
                SupportWeaponPath,
                JsonSerializer.Serialize(globalWarningSettings, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8
            );
        }

        private void ApplyPresetSupportWeaponSettings(SupportWeaponAssistSettings presetSettings)
        {
            var next = _supportWeaponSettings.Normalized();
            var preset = presetSettings.Normalized();

            // 경고음 음량/파일은 전체 공통으로 유지하고, 게이지 모양과 모드는 프리셋별 값으로 덮어쓴다.
            next.Mode = preset.Mode;
            next.GaugeVisible = preset.GaugeVisible;
            next.GaugeAlwaysRefresh = preset.GaugeAlwaysRefresh;
            next.GaugeOpacity = preset.GaugeOpacity;
            next.GaugeOffsetX = preset.GaugeOffsetX;
            next.GaugeOffsetY = preset.GaugeOffsetY;
            next.GaugeVerticalMode = preset.GaugeVerticalMode;
            next.VerticalGaugeOffsetX = preset.VerticalGaugeOffsetX;
            next.VerticalGaugeOffsetY = preset.VerticalGaugeOffsetY;
            next.GaugeWidth = preset.GaugeWidth;
            next.GaugeHeight = preset.GaugeHeight;
            next.AutoFireReleaseSeconds = preset.AutoFireReleaseSeconds;
            _supportWeaponSettings = next.Normalized();
            _supportWeaponMode = _supportWeaponSettings.Mode;
            ResetSupportWeaponChargeState();
            UpdateSupportWeaponGaugeOverlay();
            RefreshSupportWeaponGaugeTimerState();
        }

        private void InitializeCrosshairOverlay()
        {
            _crosshairTimer = new System.Windows.Forms.Timer { Interval = CrosshairOverlayRefreshIntervalMs };
            _crosshairTimer.Tick += (_, _) =>
            {
                // 창 위치와 조준점 렌더링은 자주 바뀌지 않으므로 100ms 주기로만 갱신한다.
                RefreshCachedGameClientCenter();
                UpdateCrosshairOverlay();
                UpdateFocusBoundHelperWindows();
                // 상시 갱신 OFF일 때도 포커스 복귀나 설정 변경 후 필요한 경우에만 25ms 타이머를 다시 깨운다.
                RefreshSupportWeaponGaugeTimerState();
            };
            if (!_pauseCrosshairTimer)
                _crosshairTimer.Start();

            _supportWeaponGaugeTimer = new System.Windows.Forms.Timer { Interval = SupportWeaponGaugeRefreshIntervalMs };
            _supportWeaponGaugeTimer.Tick += (_, _) =>
            {
                // 지원무기 게이지 진행률은 좌클릭 홀드 시간에 맞춰 더 촘촘하게 갱신한다.
                UpdateSupportWeaponGaugeOverlay();
                RefreshSupportWeaponGaugeTimerState();
            };
            RefreshSupportWeaponGaugeTimerState();
        }

        private void InitializeAutoReloadDetection()
        {
            // 재장전 OCR은 발사 입력 때만 수행한다. 타이머 인스턴스는 종료 처리와 기존 설정 호환을 위해 보관한다.
            _autoReloadDetectionTimer = new System.Windows.Forms.Timer { Interval = 180 };
        }

        private async Task TryTriggerAutoReloadFromPrimaryAttackAsync()
        {
            if (!_autoReloadEnabled || !IsGameActive() || _isChat)
                return;

            await _autoReloadCheckGate.WaitAsync();
            try
            {
                AutoReloadDetectionResult result = await DetectReloadPromptAsync();
                DateTime now = DateTime.UtcNow;
                _lastAutoReloadDetection = result;

                if (!result.IsEmpty || now - _lastAutoReloadAttemptAt < TimeSpan.FromMilliseconds(100))
                    return;

                // 중앙 안내가 실제로 보인 발사 시점에만 R을 탭한다. 쿨타임은 입력 직후부터 0.1초다.
                TriggerAutoReloadTap();
            }
            catch
            {
                _lastAutoReloadDetection = AutoReloadDetectionResult.Empty with { Note = "장전 안내 OCR에 실패했습니다." };
            }
            finally
            {
                _autoReloadCheckGate.Release();
            }
        }

        private async Task TryTriggerAutoReloadAfterPrimaryReleaseAsync()
        {
            // 발사키를 놓은 직후에는 게임 HUD가 갱신되는 시간을 짧게 준 뒤 같은 안내를 한 번 더 확인한다.
            await Task.Delay(100);
            await TryTriggerAutoReloadFromPrimaryAttackAsync();
        }

        private async void TriggerAutoReloadTap()
        {
            try
            {
                SendInput((uint)Keys.R, true);
                // 실제 재장전 명령을 보낸 뒤부터만 0.1초 재시도 제한을 적용한다.
                _lastAutoReloadAttemptAt = DateTime.UtcNow;
                await Task.Delay(Math.Min(Math.Max(_inputDelay, 25), 60));
            }
            finally
            {
                SendInput((uint)Keys.R, false);
            }
        }

        private void InitializeSoftwareCursorOverlay()
        {
            _softwareCursorTimer = new System.Windows.Forms.Timer { Interval = 8 };
            _softwareCursorTimer.Tick += (_, _) => UpdateSoftwareCursorOverlay();
            if (!_pauseSoftwareCursorTimer)
                _softwareCursorTimer.Start();
        }
        private static void SetTimerPauseOption(string timer, bool paused)
        {
            switch (timer)
            {
                case "crosshair": _pauseCrosshairTimer = paused; break;
                case "supportWeapon": _pauseSupportWeaponTimer = paused; break;
                case "softwareCursor": _pauseSoftwareCursorTimer = paused; break;
                case "audioMute": _pauseAudioMuteTimer = paused; break;
                case "gamepad": _pauseGamepadLoop = paused; break;
            }
        }

        private void ApplyTimerPauseOptions()
        {
            // 일시정지 토글은 현재 실행 중인 타이머에도 즉시 반영해 재시작 없이 한 항목씩 비교할 수 있게 한다.
            if (_crosshairTimer != null)
            {
                if (!_pauseCrosshairTimer)
                {
                    _crosshairTimer.Start();
                    RefreshCachedGameClientCenter();
                    UpdateCrosshairOverlay();
                }
                else
                {
                    _crosshairTimer.Stop();
                }
            }

            RefreshSupportWeaponGaugeTimerState();

            if (_softwareCursorTimer != null)
            {
                if (!_pauseSoftwareCursorTimer) _softwareCursorTimer.Start();
                else
                {
                    _softwareCursorTimer.Stop();
                    _softwareCursorOverlayForm?.Hide();
                }
            }

            if (_inactiveGameAudioMuteTimer != null)
            {
                if (!_pauseAudioMuteTimer)
                {
                    _inactiveGameAudioMuteTimer.Start();
                    UpdateInactiveGameAudioMute();
                }
                else
                {
                    _inactiveGameAudioMuteTimer.Stop();
                    HelldiversAudioMuteController.RestoreGameSessions(_gameAudioMuteStatesBeforeHelper);
                    _lastObservedGameActive = null;
                }
            }

            RefreshPadLoopState();
        }

        private void UpdateSoftwareCursorOverlay()
        {
            IntPtr activeOverlayHandle = GetSoftwareCursorAnchorHandle();
            if (activeOverlayHandle == IntPtr.Zero)
            {
                _softwareCursorAnchorHandle = IntPtr.Zero;
                _softwareCursorOverlayForm?.Hide();
                return;
            }

            if (_softwareCursorOverlayForm == null || _softwareCursorOverlayForm.IsDisposed)
                _softwareCursorOverlayForm = new SoftwareCursorOverlayForm();

            // 게임이나 Lossless Scaling이 시스템 커서를 숨겨도 F3/F4 보조창 조작 위치는 보이도록 직접 그린다.
            _softwareCursorOverlayForm.UpdateCursor(Cursor.Position);
            bool anchorChanged = _softwareCursorAnchorHandle != activeOverlayHandle;
            if (!_softwareCursorOverlayForm.Visible)
            {
                _softwareCursorOverlayForm.Show();
                ApplyCaptureExclusion(_softwareCursorOverlayForm);
                anchorChanged = true;
            }

            if (anchorChanged)
            {
                // F3/F4 창을 바로 전환하면 새 창이 커서보다 위로 올라오므로, 전환 순간에만 커서를 다시 최상단으로 올린다.
                _softwareCursorAnchorHandle = activeOverlayHandle;
                _softwareCursorOverlayForm.EnsureTopMost();
            }
        }

        private async void RestoreSoftwareCursorAfterOverlayMouseInput()
        {
            IntPtr activeOverlayHandle = GetSoftwareCursorAnchorHandle();
            SoftwareCursorOverlayForm? cursorOverlay = _softwareCursorOverlayForm;
            if (_pauseSoftwareCursorTimer
                || activeOverlayHandle == IntPtr.Zero
                || cursorOverlay == null
                || cursorOverlay.IsDisposed
                || !cursorOverlay.Visible)
            {
                return;
            }

            // WebView 클릭이 F3/F4 창을 포인터 위로 올릴 수 있어 입력 순간에 먼저 Z 순서를 복구한다.
            cursorOverlay.UpdateCursor(Cursor.Position);
            cursorOverlay.EnsureTopMost();

            // 실제 클릭 처리가 끝난 다음 프레임에도 한 번 더 올려 WebView 자식 창의 늦은 Z 순서 변경을 덮는다.
            await Task.Delay(16);
            activeOverlayHandle = GetSoftwareCursorAnchorHandle();
            if (IsDisposed
                || Disposing
                || activeOverlayHandle == IntPtr.Zero
                || cursorOverlay.IsDisposed
                || !cursorOverlay.Visible)
            {
                return;
            }

            _softwareCursorAnchorHandle = activeOverlayHandle;
            cursorOverlay.UpdateCursor(Cursor.Position);
            cursorOverlay.EnsureTopMost();
        }

        private IntPtr GetSoftwareCursorAnchorHandle()
        {
            if (_helperEditorWindow is { IsDisposed: false, Visible: true, IsHandleCreated: true })
                return _helperEditorWindow.Handle;

            if (_presetOverlayForm is { IsDisposed: false, Visible: true, IsHandleCreated: true })
                return _presetOverlayForm.Handle;

            return IntPtr.Zero;
        }
        private void InitializeInactiveGameAudioMute()
        {
            _inactiveGameAudioMuteTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _inactiveGameAudioMuteTimer.Tick += (_, _) => UpdateInactiveGameAudioMute();
            if (!_pauseAudioMuteTimer)
                _inactiveGameAudioMuteTimer.Start();
        }

        private void UpdateInactiveGameAudioMute()
        {
            if (!_muteGameAudioWhenInactive)
            {
                // 기능 OFF에서는 헬퍼가 걸었던 음소거만 한 번 복구하고 포커스 상태를 초기화한다.
                if (_gameAudioMuteStatesBeforeHelper.Count > 0)
                    HelldiversAudioMuteController.RestoreGameSessions(_gameAudioMuteStatesBeforeHelper);
                _lastObservedGameActive = null;
                return;
            }

            // 조준점/지원무기 게이지와 똑같은 게임 포커스 판정을 사용해 창 추적 상태 불일치를 없앤다.
            bool isGameActive = IsGameActive();

            if (!_lastObservedGameActive.HasValue)
            {
                // 기능을 켠 순간 이미 비활성이어도 다음 전환을 기다리지 않고 즉시 현재 상태를 적용한다.
                _lastObservedGameActive = isGameActive;
                if (isGameActive)
                    HelldiversAudioMuteController.ForceUnmuteGameSessions();
                else
                    HelldiversAudioMuteController.MuteGameSessions(_gameAudioMuteStatesBeforeHelper);
                return;
            }

            if (_lastObservedGameActive.Value == isGameActive)
                return;

            // CoreAudio 세션 열거는 포커스 상태가 실제로 바뀐 이 순간에만 한 번 수행한다.
            _lastObservedGameActive = isGameActive;
            if (isGameActive)
            {
                if (_gameAudioMuteStatesBeforeHelper.Count > 0)
                    HelldiversAudioMuteController.RestoreGameSessions(_gameAudioMuteStatesBeforeHelper);
                // 게임 재시작으로 오디오 세션 ID가 바뀌었어도 활성화 순간에는 새 세션을 확실히 해제한다.
                HelldiversAudioMuteController.ForceUnmuteGameSessions();
                return;
            }

            HelldiversAudioMuteController.MuteGameSessions(_gameAudioMuteStatesBeforeHelper);
        }

        private bool RefreshCachedGameClientCenter()
        {
            if (!IsGameActive() || !TryGetGameWindow(out IntPtr hwnd) || !GetClientRect(hwnd, out Rectangle clientRect))
            {
                _hasCachedGameClientCenter = false;
                return false;
            }

            Point topLeft = new(clientRect.Left, clientRect.Top);
            ClientToScreen(hwnd, ref topLeft);
            _cachedGameClientCenter = new Point(topLeft.X + clientRect.Width / 2, topLeft.Y + clientRect.Height / 2);
            _hasCachedGameClientCenter = true;
            return true;
        }

        private bool TryGetCachedGameClientCenter(out Point center)
        {
            // 지원무기 게이지 25ms tick에서는 창 좌표 API를 매번 부르지 않고 100ms 캐시를 우선 사용한다.
            if (_hasCachedGameClientCenter || RefreshCachedGameClientCenter())
            {
                center = _cachedGameClientCenter;
                return true;
            }

            center = Point.Empty;
            return false;
        }

        private void UpdateCrosshairOverlay()
        {
            int displayOpacityPercent = 100;
            bool shouldRespectRightMouse = _crosshairSettings.ShowOnlyWhileRightMouseButtonHeld;
            if (shouldRespectRightMouse && !IsRightMouseButtonHeld())
                // 우클릭 조준 표시 모드에서는 비조준 상태를 숨김이 아니라 별도 투명도 배율로 다룬다.
                displayOpacityPercent = _crosshairSettings.NonAimingOpacity;

            bool shouldShowCrosshair = _crosshairSettings.Enabled
                && displayOpacityPercent > 0
                && IsGameActive()
                && !_isChat
                && !CursorUtil.IsVisible();

            if (shouldShowCrosshair)
            {
                if (!TryGetCachedGameClientCenter(out Point center)) return;

                // 게임 창 중앙 기준으로 조준점 폼을 이동시켜 해상도/창모드 변화에도 따라가게 한다.
                if (_crosshairForm == null || _crosshairForm.IsDisposed)
                    _crosshairForm = new CrosshairForm(_crosshairSettings);

                _crosshairForm.ApplySettings(_crosshairSettings, center, displayOpacityPercent);
                if (!_crosshairForm.Visible) _crosshairForm.Show();
                ApplyCaptureExclusion(_crosshairForm);
                _crosshairForm.EnsureTopMost();
                return;
            }

            _crosshairForm?.Hide();
        }

        private bool IsRightMouseButtonHeld()
        {
            // 조준점 표시 조건은 후킹 상태와 실제 키 상태를 같이 확인해 우클릭 release 누락으로 켜진 채 남는 일을 막는다.
            bool physicallyHeld = (GetAsyncKeyState((int)Keys.RButton) & 0x8000) != 0;
            if (!physicallyHeld)
                _isRightMouseButtonDown = false;

            return physicallyHeld || _isRightMouseButtonDown;
        }

        private bool IsLeftMouseButtonHeld()
        {
            bool physicallyHeld = (GetAsyncKeyState((int)Keys.LButton) & 0x8000) != 0;
            if (_supportWeaponSuppressLeftUntilRelease)
            {
                // 자동사격이 LEFTUP을 보낸 뒤에는 실제 마우스를 한 번 놓기 전까지 같은 홀드 입력을 다시 차지로 취급하지 않는다.
                if (physicallyHeld)
                    return false;

                _supportWeaponSuppressLeftUntilRelease = false;
                _supportWeaponAutoReleased = false;
                _isLeftMouseButtonDown = false;
                return false;
            }

            if (!physicallyHeld)
                _isLeftMouseButtonDown = false;

            return physicallyHeld || _isLeftMouseButtonDown;
        }

        private void UpdateSupportWeaponPauseFromWeaponKey(uint vkCode)
        {
            bool ctrl = IsPhysicalKeyDown(Keys.ControlKey) || IsPhysicalKeyDown(Keys.LControlKey) || IsPhysicalKeyDown(Keys.RControlKey);
            bool alt = IsPhysicalKeyDown(Keys.Menu) || IsPhysicalKeyDown(Keys.LMenu) || IsPhysicalKeyDown(Keys.RMenu);

            if (vkCode is (uint)Keys.D1 or (uint)Keys.NumPad1 or (uint)Keys.D2 or (uint)Keys.NumPad2 or (uint)Keys.D4 or (uint)Keys.NumPad4)
            {
                // 주/보조/수류탄 슬롯으로 전환한 상태에서는 지원무기 차지 게이지가 뜨지 않도록 일시정지한다.
                _supportWeaponPausedByWeaponKey = true;
                ResetSupportWeaponChargeState();
                _supportWeaponGaugeForm?.Hide();
                RefreshSupportWeaponGaugeTimerState();
                return;
            }

            if ((vkCode is (uint)Keys.D3 or (uint)Keys.NumPad3) && !ctrl && !alt)
            {
                // 3번 지원무기 슬롯으로 돌아오면 기존 모드 설정은 유지한 채 보조 기능만 다시 켠다.
                _supportWeaponPausedByWeaponKey = false;
                ResetSupportWeaponChargeState();
                UpdateSupportWeaponGaugeOverlay();
                RefreshSupportWeaponGaugeTimerState();
            }
        }

        private void ResetSupportWeaponChargeState()
        {
            _supportWeaponSuppressLeftUntilRelease = false;
            _supportWeaponAutoReleased = false;
            _leftMouseDownStartedAt = DateTime.MinValue;
            _supportWeaponWarningPlayed = false;
        }

        private bool ShouldRunSupportWeaponGaugeTimer()
        {
            if (_pauseSupportWeaponTimer)
                return false;

            if (!IsGameActive() || _isChat || _supportWeaponPausedByWeaponKey)
                return false;

            if (_supportWeaponMode == "Off")
                return false;

            // 상시 갱신 OFF에서는 실제 발사 입력이 있는 동안에만 25ms 타이머를 사용한다.
            return _supportWeaponSettings.GaugeAlwaysRefresh
                || _isLeftMouseButtonDown
                || (GetAsyncKeyState((int)Keys.LButton) & 0x8000) != 0;
        }

        private void RefreshSupportWeaponGaugeTimerState()
        {
            if (_supportWeaponGaugeTimer == null)
                return;

            bool shouldRun = ShouldRunSupportWeaponGaugeTimer();
            if (shouldRun && !_supportWeaponGaugeTimer.Enabled)
                _supportWeaponGaugeTimer.Start();
            else if (!shouldRun && _supportWeaponGaugeTimer.Enabled)
                _supportWeaponGaugeTimer.Stop();
        }

        private static string GetSupportWeaponModeDisplayName(string mode)
        {
            return mode switch
            {
                "AutoFire" => "자동사격",
                "AutoRepeat" => "자동 연속 사격",
                "Danger" => "위험표시",
                _ => "끄기"
            };
        }

        private void UpdateSupportWeaponGaugeOverlay()
        {
            DateTime now = DateTime.UtcNow;
            bool gaugeEnabled = _supportWeaponMode != "Off" && !_supportWeaponPausedByWeaponKey;

            if (!IsGameActive() || _isChat || CursorUtil.IsVisible() || !gaugeEnabled)
            {
                _supportWeaponGaugeForm?.Hide();
                return;
            }

            bool leftHeld = IsLeftMouseButtonHeld();
            if (!leftHeld)
            {
                _leftMouseDownStartedAt = DateTime.MinValue;
                _supportWeaponWarningPlayed = false;
            }
            else if (_leftMouseDownStartedAt == DateTime.MinValue)
            {
                _leftMouseDownStartedAt = now;
                _supportWeaponWarningPlayed = false;
            }

            double elapsedSeconds = leftHeld ? Math.Max(0, (now - _leftMouseDownStartedAt).TotalSeconds) : 0;
            if (gaugeEnabled && leftHeld && elapsedSeconds >= 2.4 && !_supportWeaponWarningPlayed)
            {
                _supportWeaponWarningPlayed = true;
                PlaySupportWeaponWarningBeep(_supportWeaponSettings.WarningVolume, _supportWeaponSettings.WarningSoundPath);
            }

            // 자동사격 해제 시간은 무기별 체감에 맞춰 사용자가 직접 조절할 수 있게 설정값을 사용한다.
            double autoFireReleaseSeconds = _supportWeaponSettings.Normalized().AutoFireReleaseSeconds;
            if (gaugeEnabled && _supportWeaponMode == "AutoFire" && leftHeld && elapsedSeconds >= autoFireReleaseSeconds && !_supportWeaponAutoReleased)
                ForceReleaseLeftMouseForSupportWeapon();
            else if (gaugeEnabled && _supportWeaponMode == "AutoRepeat" && leftHeld && elapsedSeconds >= autoFireReleaseSeconds && !_supportWeaponAutoReleased)
                ForceRepeatLeftMouseForSupportWeapon();

            if (!_supportWeaponSettings.GaugeVisible)
            {
                // 게이지 표시를 꺼도 자동사격/경고 계산은 위에서 계속 수행하고, 오버레이 창만 숨긴다.
                _supportWeaponGaugeForm?.Hide();
                return;
            }

            if (!TryGetCachedGameClientCenter(out Point center))
            {
                _supportWeaponGaugeForm?.Hide();
                return;
            }

            double progress = gaugeEnabled ? Math.Clamp(elapsedSeconds / 2.95, 0, 1) : 0;

            if (_supportWeaponGaugeForm == null || _supportWeaponGaugeForm.IsDisposed)
                _supportWeaponGaugeForm = new SupportWeaponGaugeForm();

            // 지원무기 게이지는 조준점 중앙을 기준으로 위치 보정값을 더해 해상도 변화에도 같은 위치를 유지한다.
            bool wasGaugeVisible = _supportWeaponGaugeForm.Visible;
            _supportWeaponGaugeForm.ApplyState(_supportWeaponSettings, center, progress, elapsedSeconds, gaugeEnabled, "", _supportWeaponMode);
            if (!wasGaugeVisible)
            {
                _supportWeaponGaugeForm.Show();
                ApplyCaptureExclusion(_supportWeaponGaugeForm);
                _supportWeaponGaugeForm.EnsureTopMost();
            }
        }

        private void ForceReleaseLeftMouseForSupportWeapon()
        {
            // 자동사격 모드는 레일건 과충전 위험 직후에 누르고 있던 좌클릭을 한 번 강제로 놓는다.
            _supportWeaponAutoReleased = true;
            _supportWeaponSuppressLeftUntilRelease = true;
            _isLeftMouseButtonDown = false;
            _leftMouseDownStartedAt = DateTime.MinValue;
            _supportWeaponWarningPlayed = false;
            mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
        }

        private void ForceRepeatLeftMouseForSupportWeapon()
        {
            // 자동 연속 사격은 발사 시점에 좌클릭을 아주 짧게 떼었다가 다시 눌러 다음 차지를 이어간다.
            _supportWeaponAutoReleased = true;
            _isLeftMouseButtonDown = true;
            _leftMouseDownStartedAt = DateTime.UtcNow;
            _supportWeaponWarningPlayed = false;
            mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(12);
            mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
            _supportWeaponAutoReleased = false;
        }

        private static void PlaySupportWeaponWarningBeep(int volume, string? soundPath)
        {
            int safeVolume = Math.Clamp(volume, 0, 100);
            if (safeVolume <= 0)
                return;

            string resolvedSoundPath = ResolveSupportWeaponWarningSoundPath(soundPath);
            if (!string.IsNullOrWhiteSpace(resolvedSoundPath))
            {
                // 사용자 지정 파일이나 기본 sounds\warning.mp3가 있으면 기본 비프음 fallback 없이 해당 파일만 재생한다.
                Task.Run(() =>
                {
                    try
                    {
                        // MP3는 MCI/WMP에서 조용히 실패하는 경우가 있어 WinRT MediaPlayer를 먼저 사용한다.
                        if (TryPlaySupportWeaponWarningFileWithWinRtMediaPlayer(resolvedSoundPath, safeVolume))
                            return;

                        if (TryPlaySupportWeaponWarningFileWithMci(resolvedSoundPath, safeVolume))
                            return;

                        TryPlaySupportWeaponWarningFileWithWindowsMedia(resolvedSoundPath, safeVolume);
                    }
                    catch { }
                });
                return;
            }

            Task.Run(() => PlayBuiltInSupportWeaponWarningBeep(safeVolume));
        }
        private static string ResolveSupportWeaponWarningSoundPath(string? soundPath)
        {
            string trimmedPath = soundPath?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(trimmedPath) && File.Exists(trimmedPath))
                return trimmedPath;

            // 경고음 파일을 따로 고르지 않았을 때는 헬퍼 실행 파일 기준 sounds\warning.mp3를 기본값으로 사용한다.
            string defaultPath = Path.Combine(AppContext.BaseDirectory, "sounds", "warning.mp3");
            return File.Exists(defaultPath) ? defaultPath : "";
        }


        private static void PlayBuiltInSupportWeaponWarningBeep(int volume)
        {
            try
            {
                using MemoryStream stream = BuildBeepWaveStream(1250, 90, volume);
                using var player = new System.Media.SoundPlayer(stream);
                player.PlaySync();
            }
            catch { }
        }

        private static bool TryPlaySupportWeaponWarningFileWithWinRtMediaPlayer(string soundPath, int volume)
        {
            try
            {
                if (!File.Exists(soundPath))
                    return false;

                var player = new Windows.Media.Playback.MediaPlayer
                {
                    AutoPlay = false,
                    Volume = Math.Clamp(volume, 0, 100) / 100.0,
                    Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(soundPath))
                };

                void ReleasePlayer()
                {
                    lock (SupportWarningMediaPlayersLock)
                    {
                        SupportWarningMediaPlayers.Remove(player);
                    }

                    player.Dispose();
                }

                player.MediaEnded += (_, _) => ReleasePlayer();
                player.MediaFailed += (_, _) => ReleasePlayer();

                lock (SupportWarningMediaPlayersLock)
                {
                    // MediaPlayer 인스턴스가 곧바로 GC되면 MP3 재생이 시작되기 전에 끊길 수 있어 종료 이벤트까지 보관한다.
                    SupportWarningMediaPlayers.Add(player);
                }

                player.Play();
                return true;
            }
            catch
            {
                return false;
            }
        }
        private static bool TryPlaySupportWeaponWarningFileWithWindowsMedia(string soundPath, int volume)
        {
            bool played = false;
            using ManualResetEventSlim finished = new(false);
            Thread thread = new(() =>
            {
                object? player = null;
                try
                {
                    Type? playerType = Type.GetTypeFromProgID("WMPlayer.OCX");
                    if (playerType == null)
                        return;

                    // MP3는 SoundPlayer가 지원하지 않아 Windows Media Player COM을 우선 사용한다.
                    player = Activator.CreateInstance(playerType);
                    dynamic mediaPlayer = player!;
                    mediaPlayer.settings.volume = Math.Clamp(volume, 0, 100);
                    mediaPlayer.URL = soundPath;
                    mediaPlayer.controls.play();

                    Stopwatch stopwatch = Stopwatch.StartNew();
                    while (stopwatch.Elapsed < TimeSpan.FromSeconds(2))
                    {
                        int state = mediaPlayer.playState;
                        if (state == 3)
                            played = true;
                        if (played && (state == 1 || state == 8))
                            break;
                        Thread.Sleep(20);
                    }

                    mediaPlayer.controls.stop();
                    mediaPlayer.close();
                }
                catch
                {
                    played = false;
                }
                finally
                {
                    if (player != null && Marshal.IsComObject(player))
                        Marshal.FinalReleaseComObject(player);
                    finished.Set();
                }
            });

            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return finished.Wait(TimeSpan.FromSeconds(3)) && played;
        }

        private static bool TryPlaySupportWeaponWarningFileWithMci(string soundPath, int volume)
        {
            string alias = "hd2_warning_" + Guid.NewGuid().ToString("N");
            string extension = Path.GetExtension(soundPath);
            string typeClause = extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ? " type mpegvideo" : "";
            int safeVolume = Math.Clamp(volume, 0, 100) * 10;

            int openResult = mciSendString($"open \"{soundPath}\"{typeClause} alias {alias}", null, 0, IntPtr.Zero);
            if (openResult != 0)
                return false;

            try
            {
                // MCI 볼륨은 0~1000 범위를 사용하므로 UI의 0~100 값을 10배로 변환한다.
                mciSendString($"setaudio {alias} volume to {safeVolume}", null, 0, IntPtr.Zero);
                return mciSendString($"play {alias} wait", null, 0, IntPtr.Zero) == 0;
            }
            finally
            {
                mciSendString($"close {alias}", null, 0, IntPtr.Zero);
            }
        }

        private void OpenSupportWarningSoundFileDialog()
        {
            using var dialog = new OpenFileDialog
            {
                Title = "지원무기 경고음 파일 선택",
                Filter = "오디오 파일 (*.wav;*.mp3)|*.wav;*.mp3|WAV 파일 (*.wav)|*.wav|MP3 파일 (*.mp3)|*.mp3|모든 파일 (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            // 경고음 파일 경로는 설정 파일에 저장해 다음 실행 때도 같은 파일을 사용한다.
            _supportWeaponSettings = _supportWeaponSettings.Normalized();
            _supportWeaponSettings.WarningSoundPath = dialog.FileName;
            SaveSupportWeaponSettings();
            SendSettingsToWeb();
        }

        private static MemoryStream BuildBeepWaveStream(int frequency, int durationMs, int volume)
        {
            const int sampleRate = 44100;
            short amplitude = (short)(short.MaxValue * Math.Clamp(volume, 0, 100) / 100.0 * 0.35);
            int sampleCount = sampleRate * durationMs / 1000;
            int dataLength = sampleCount * 2;
            MemoryStream stream = new();
            using (BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataLength);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(sampleRate);
                writer.Write(sampleRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(dataLength);

                for (int i = 0; i < sampleCount; i++)
                {
                    double t = i / (double)sampleRate;
                    short sample = (short)(Math.Sin(2 * Math.PI * frequency * t) * amplitude);
                    writer.Write(sample);
                }
            }
            stream.Position = 0;
            return stream;
        }
        private void OpenCrosshairEditor()
        {
            if (_crosshairEditorForm == null || _crosshairEditorForm.IsDisposed)
            {
                // 제작기는 WebView 모달 제약을 피하려고 완전히 별도 WinForms 창으로 띄운다.
                _crosshairEditorForm = new CrosshairEditorForm(_crosshairSettings, ApplyCrosshairSettingsFromEditor);
                _crosshairEditorForm.FormClosed += (_, _) => _crosshairEditorForm = null;
            }

            _crosshairEditorForm.ApplySettings(_crosshairSettings);
            _crosshairEditorForm.Show();
            ApplyCaptureExclusion(_crosshairEditorForm);
            _crosshairEditorForm.Activate();
        }

        private void OpenOcrRegionSettings()
        {
            if (_ocrRegionSettingsForm == null || _ocrRegionSettingsForm.IsDisposed)
            {
                // OCR 영역 조절은 게임 화면 좌표를 다루므로 WebView가 아닌 별도 설정창에서 숫자로 바로 편집한다.
                _ocrRegionSettingsForm = new OcrRegionSettingsForm(_ocrRegionSettings, ApplyOcrRegionSettings, PreviewOcrRegion, TestOcrRegionText);
                _ocrRegionSettingsForm.FormClosed += (_, _) =>
                {
                    HideOcrRegionOverlay();
                    _ocrRegionSettingsForm = null;
                };
            }

            _ocrRegionSettingsForm.ApplySettings(_ocrRegionSettings);
            _ocrRegionSettingsForm.Show();
            ApplyCaptureExclusion(_ocrRegionSettingsForm);
            _ocrRegionSettingsForm.Activate();
        }

        private void OpenAutoReloadCalibration()
        {
            if (_autoReloadCalibrationForm == null || _autoReloadCalibrationForm.IsDisposed)
            {
                // 자동 재장전 감지기는 OCR과 독립된 픽셀 판독 모듈이라 전용 보정 창에서만 영역과 민감도를 다룬다.
                _autoReloadCalibrationForm = new AutoReloadCalibrationForm(
                    _autoReloadSettings,
                    ApplyAutoReloadSettings,
                    PreviewAutoReloadRegion,
                    TestAutoReloadDetectionAsync);
                _autoReloadCalibrationForm.FormClosed += (_, _) =>
                {
                    HideOcrRegionOverlay();
                    _autoReloadCalibrationForm = null;
                };
            }

            _autoReloadCalibrationForm.ApplySettings(_autoReloadSettings);
            _autoReloadCalibrationForm.Show();
            ApplyCaptureExclusion(_autoReloadCalibrationForm);
            _autoReloadCalibrationForm.Activate();
        }

        private void ApplyAutoReloadSettings(AutoReloadSettings settings)
        {
            _autoReloadSettings = settings.Normalized();
            SaveSetting();
        }

        private void PreviewAutoReloadRegion(AutoReloadSettings settings)
        {
            AutoReloadSettings normalized = settings.Normalized();
            if (TryBuildGameScreenRect(normalized.X, normalized.Y, normalized.Width, normalized.Height, out Rectangle region))
                // 실제 인식 중에는 표시하지 않고, 전용 보정 창을 열었을 때만 빨간 테두리로 대상 범위를 안내한다.
                ShowOcrRegionOverlay(region, normalized.BorderThickness, null);
        }

        private Task<AutoReloadDetectionResult> TestAutoReloadDetectionAsync(AutoReloadSettings settings)
        {
            return DetectReloadPromptAsync(settings);
        }

        private void ApplyOcrRegionSettings(Dictionary<string, OcrRegionSettings> settings)
        {
            // 조절창에서 바꾼 OCR 영역은 다음 자동선택부터 즉시 쓰이도록 메모리와 settings.ini에 저장한다.
            _ocrRegionSettings.Clear();
            foreach (var (type, region) in settings)
                _ocrRegionSettings[type] = region.Normalized();

            SaveSetting();
        }

        private void ApplyCrosshairSettingsFromEditor(CrosshairSettings settings)
        {
            // 제작 창의 변경값은 실제 오버레이, 설정 파일, WebView 체크박스에 즉시 반영한다.
            _crosshairSettings = settings.Normalized();
            SaveCrosshairSettings();
            UpdateCrosshairOverlay();
            SendSettingsToWeb();
        }

        private async void WarmupOcr()
        {
            try
            {
                _ocrEngine = CreateKoreanOcrEngine();
                if (_ocrEngine == null) return;

                using var dummy = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 10, 10, BitmapAlphaMode.Premultiplied);
                await _ocrEngine.RecognizeAsync(dummy);
            }
            catch
            {
                _ocrEngine = null;
            }
        }

        private Image? GetStratagemImage(string name)
        {
            if (_imageCache.TryGetValue(name, out var cached))
                return cached;

            string path = Path.Combine(AppContext.BaseDirectory, "images", "stratagems", $"{name}.png");
            if (!File.Exists(path))
            {
                _imageCache[name] = null;
                return null;
            }

            using (var original = Image.FromFile(path))
            {
                var resized = new Bitmap(original, new Size(100, 100));
                _imageCache[name] = resized;
                return resized;
            }
        }

        public static bool IsGameActive()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            var className = new StringBuilder(256);
            return GetClassName(hwnd, className, className.Capacity) != 0 &&
                   className.ToString() == "stingray_window";
        }

        private void StartPadLoop()
        {
            _padLoopCts?.Cancel();

            _padLoopCts = new CancellationTokenSource();
            var token = _padLoopCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    var padEvents = GamepadReader.GetButtonEvents();
                    foreach (var ev in padEvents)
                    {
                        uint pressedPadButton = (uint)ev.Button;

                        if (_isWaitingForKey && IsSettingsKeyCaptureAllowed() && ev.Pressed)
                        {
                            this.BeginInvoke(new Action(() =>
                            {
                                AssignCapturedSettingsKey(pressedPadButton);
                            }));
                            continue;
                        }

                        if (pressedPadButton == _overlayKey)
                        {
                            this.BeginInvoke(new Action(() =>
                            {
                                if (ev.Pressed)
                                {
                                    // 실제 오버레이가 열린 경우에만 게임의 스트라타젬 보기 키를 누른다.
                                    if (OverlayShow()) SetOverlayStratagemViewKey(true);
                                }
                                else
                                {
                                    SetOverlayStratagemViewKey(false);
                                    OverlayHide();
                                }
                            }));
                        }
                        else if (pressedPadButton == _autoSelectKey && ev.Pressed)
                        {
                            TriggerOrCancelAutoSelection();
                        }
                        else if (pressedPadButton == _reinforceKey && ev.Pressed)
                        {
                            TriggerStratagem(-1);
                        }
                        else if (pressedPadButton == _crosshairToggleKey && ev.Pressed)
                        {
                            this.BeginInvoke(new Action(ToggleCrosshairEnabled));
                        }
                        else if (pressedPadButton == _helperEditorKey && ev.Pressed && IsGameActive())
                        {
                            // F3 편집창은 헬다이버즈2가 활성화된 순간에만 열고 닫는다.
                            this.BeginInvoke(new Action(ToggleHelperEditorWindow));
                        }
                        else if (pressedPadButton == _presetOverlayKey && ev.Pressed && IsGameActive())
                        {
                            // F4 선택창도 게임 포커스가 있을 때만 반응해 다른 창에서 오작동하지 않게 한다.
                            this.BeginInvoke(new Action(TogglePresetOverlay));
                        }
                        else if (ev.Pressed)
                        {
                            foreach (var pair in _slotKey)
                            {
                                if (pressedPadButton == pair.Value)
                                {
                                    TriggerStratagem(pair.Key);
                                    break;
                                }
                            }
                        }
                    }

                    var stick = GamepadReader.GetRightStick();
                    if (_overlayForm != null)
                    {
                        float speed = 30f;
                        float moveX = stick.dx * speed;
                        float moveY = stick.dy * speed;

                        int dx = (int)Math.Round(moveX);
                        int dy = (int)Math.Round(moveY);

                        if (dx != 0 || dy != 0)
                            mouse_event(0x0001, (uint)dx, (uint)dy, 0, IntPtr.Zero);
                    }

                    await Task.Delay(16, token);
                }
            }, token);
        }

        private void RefreshPadLoopState()
        {
            if (!_pauseGamepadLoop)
            {
                if (_padLoopCts == null || _padLoopCts.IsCancellationRequested)
                    StartPadLoop();
                return;
            }

            // 게임패드 테스트 스위치를 끄면 16ms 폴링 작업 자체를 취소하고 다음 ON에서 새 루프를 만든다.
            CancellationTokenSource? current = _padLoopCts;
            _padLoopCts = null;
            current?.Cancel();
            current?.Dispose();
        }

        private void HandleHookInput(InputEventArgs e)
        {
            uint vkCode = e.VirtualKey;

            if (_heldOverlayStratagemViewKey != 0 && (!IsGameActive() || _isChat))
            {
                // 포커스 변경과 채팅 전환 사이에 단축키 KeyUp이 누락돼도 주입한 보기 키는 즉시 복구한다.
                ReleaseOverlayStratagemViewKey("게임 비활성화 또는 채팅 전환");
            }

            bool isMouseButton = vkCode == (uint)Keys.LButton
                || vkCode == (uint)Keys.RButton
                || vkCode == (uint)Keys.MButton
                || vkCode == (uint)Keys.XButton1
                || vkCode == (uint)Keys.XButton2;
            if (isMouseButton && !e.IsInjected)
            {
                // 클릭으로 F3/F4의 WebView가 앞으로 나오는 경우에만 소프트웨어 포인터를 다시 최상단으로 올린다.
                RestoreSoftwareCursorAfterOverlayMouseInput();
            }

            if (e.IsDown && !e.IsInjected)
                UpdateSupportWeaponPauseFromWeaponKey(vkCode);

            if (vkCode == (uint)Keys.RButton)
                _isRightMouseButtonDown = e.IsDown;

            if (vkCode == (uint)Keys.LButton)
            {
                _isLeftMouseButtonDown = e.IsDown;
                if (e.IsDown)
                {
                    if (!_supportWeaponSuppressLeftUntilRelease)
                    {
                        _leftMouseDownStartedAt = DateTime.UtcNow;
                        _supportWeaponWarningPlayed = false;
                        _supportWeaponAutoReleased = false;
                    }
                }
                else
                {
                    _supportWeaponSuppressLeftUntilRelease = false;
                    _supportWeaponAutoReleased = false;
                    _leftMouseDownStartedAt = DateTime.MinValue;
                    _supportWeaponWarningPlayed = false;
                }

                // 상시 갱신을 꺼도 실제 발사 입력이 시작되면 즉시 25ms 차지 계산을 시작하고, 해제 시 마지막 상태를 반영한다.
                UpdateSupportWeaponGaugeOverlay();
                RefreshSupportWeaponGaugeTimerState();

                if (!e.IsInjected)
                {
                    if (e.IsDown)
                    {
                        // 발사키를 누른 직후의 빈 탄창 안내를 즉시 확인한다.
                        _ = TryTriggerAutoReloadFromPrimaryAttackAsync();
                    }
                    else
                    {
                        // 발사키 해제 후 HUD가 한 프레임 갱신된 시점에도 한 번 더 확인한다.
                        _ = TryTriggerAutoReloadAfterPrimaryReleaseAsync();
                    }
                }
            }

            if (e.IsDown && vkCode == (uint)Keys.Escape && !e.IsInjected && TryCancelAutoSelection())
                return;

            if (_isWaitingForKey && e.IsDown && !e.IsInjected)
            {
                // F3 편집창은 게임 포커스를 유지하는 비활성 오버레이라 ActiveForm 검사에만 의존하면
                // 설정 대기 중에도 키가 전달되지 않는다. 사용자가 설정 행을 눌러 시작한 대기 상태에서는
                // 프로그램이 주입한 키를 제외한 실제 입력을 포커스와 관계없이 바로 할당한다.
                AssignCapturedSettingsKey(vkCode);
                return;
            }

            if (vkCode == _overlayKey)
            {
                if (e.IsDown)
                {
                    // 슬롯이 없거나 커서가 보이는 상황에서는 보기 키만 단독으로 눌리지 않게 한다.
                    if (OverlayShow()) SetOverlayStratagemViewKey(true);
                }
                else
                {
                    SetOverlayStratagemViewKey(false);
                    OverlayHide();
                }
            }

            if (!e.IsDown)
                return;

            if (vkCode == _autoSelectKey)
            {
                TriggerOrCancelAutoSelection();
            }
            else if (vkCode == _reinforceKey)
            {
                TriggerStratagem(-1);
            }
            else if (vkCode == _crosshairToggleKey)
            {
                ToggleCrosshairEnabled();
            }
            else if (vkCode == _helperEditorKey)
            {
                // 독립된 창 열기 단축키는 헬다이버즈2가 전면에 있을 때만 프리셋 편집창을 토글한다.
                if (IsGameActive())
                    ToggleHelperEditorWindow();
            }
            else if (vkCode == _presetOverlayKey)
            {
                // 프리셋 전환 단축키도 게임 포커스 밖에서는 무시한다.
                if (IsGameActive())
                    TogglePresetOverlay();
            }
            else
            {
                foreach (var pair in _slotKey)
                {
                    if (pair.Value == vkCode)
                    {
                        TriggerStratagem(pair.Key);
                        break;
                    }
                }
            }
        }

        private void TriggerOrCancelAutoSelection()
        {
            // 자동선택 실행 중 같은 단축키를 다시 누르면 취소하고, 실행 중이 아닐 때만 새 자동선택을 시작한다.
            if (TryCancelAutoSelection())
                return;

            TriggerAutoSelection();
        }
        private bool TryCancelAutoSelection()
        {
            var cts = _autoSelectionCts;
            if (cts == null || cts.IsCancellationRequested)
                return false;

            // 자동선택은 여러 OCR/키 입력 단계로 이어지므로 자동선택 단축키 재입력을 취소 토큰으로 전달한다.
            cts.Cancel();
            return true;
        }

        private bool IsSettingsKeyCaptureAllowed()
        {
            // F3 편집창은 게임 포커스를 유지하는 비활성 창이라 ActiveForm이 되지 않으므로, 보이는 상태도 키 캡처 대상으로 인정한다.
            return Form.ActiveForm is MainForm
                || Form.ActiveForm is HelperEditorWindow
                || (_helperEditorWindow is { IsDisposed: false, Visible: true });
        }

        private bool TryRouteEditorKeyboardInput(uint vkCode, bool isDown)
        {
            if (!IsGameActive() || _isPad || _isWaitingForKey)
                return false;

            if (_helperEditorWindow is not { IsDisposed: false, Visible: true } editor || !editor.CanReceiveForwardedKeyboardInput)
            {
                ResetEditorForwardedText();
                return false;
            }

            if (vkCode == _helperEditorKey || vkCode == _presetOverlayKey)
            {
                ResetEditorForwardedText();
                return false;
            }

            bool alt = IsPhysicalKeyDown(Keys.Menu) || IsPhysicalKeyDown(Keys.LMenu) || IsPhysicalKeyDown(Keys.RMenu);
            if (IsEditorAltKey(vkCode) || alt)
            {
                // Alt+Tab 같은 Windows 전환 조합은 편집창 라우팅이 먹지 않고 운영체제에 그대로 넘긴다.
                ResetEditorForwardedText();
                return false;
            }

            bool ctrl = _editorForwardedCtrlDown || IsPhysicalKeyDown(Keys.ControlKey) || IsPhysicalKeyDown(Keys.LControlKey) || IsPhysicalKeyDown(Keys.RControlKey);
            bool shift = _editorForwardedShiftDown || IsPhysicalKeyDown(Keys.ShiftKey) || IsPhysicalKeyDown(Keys.LShiftKey) || IsPhysicalKeyDown(Keys.RShiftKey);
            if (vkCode is (uint)Keys.ControlKey or (uint)Keys.LControlKey or (uint)Keys.RControlKey)
            {
                _editorForwardedCtrlDown = isDown;
                ctrl = isDown;
            }
            if (vkCode is (uint)Keys.ShiftKey or (uint)Keys.LShiftKey or (uint)Keys.RShiftKey)
            {
                _editorForwardedShiftDown = isDown;
                shift = isDown;
            }

            // F3 편집창은 비활성 창이라 WebView 클릭 이벤트의 shiftKey/ctrlKey가 비어 있을 수 있어 후킹 상태를 별도로 전달한다.
            editor.SetModifierStateFromHook(ctrl, shift);
            if (IsEditorModifierKey(vkCode))
                return true;

            if (!IsEditorInputCandidate(vkCode, ctrl))
                return false;

            if (!isDown)
                return true;

            if (vkCode == (uint)Keys.HangulMode)
            {
                FlushEditorHangulComposition(editor);
                _editorHangulMode = !_editorHangulMode;
                return true;
            }

            if (ctrl)
            {
                FlushEditorHangulComposition(editor);
                RouteEditorShortcut(editor, vkCode);
                return true;
            }

            bool isAlphabet = vkCode >= (uint)Keys.A && vkCode <= (uint)Keys.Z;
            if (_editorHangulMode && isAlphabet)
            {
                if (_editorHangulEngine.ProcessInput(vkCode, shift))
                {
                    RouteEditorHangulDiff(editor, _editorHangulEngine.GetCurrentText());
                    return true;
                }
            }

            if (vkCode == (uint)Keys.Back && (_editorHangulEngine.IsComposing() || _editorLastInjected.Length > 0))
            {
                _editorHangulEngine.Backspace();
                RouteEditorHangulDiff(editor, _editorHangulEngine.GetCurrentText());
                return true;
            }

            FlushEditorHangulComposition(editor);

            if (TryGetEditorTextForKey(vkCode, shift, out string? text))
            {
                editor.InsertTextFromHook(0, text!);
                return true;
            }

            if (TryGetEditorEditingKey(vkCode, out string? editingKey))
            {
                editor.SendEditingKeyFromHook(editingKey!);
                return true;
            }

            return true;
        }

        private void RouteEditorShortcut(HelperEditorWindow editor, uint vkCode)
        {
            switch ((Keys)vkCode)
            {
                case Keys.A:
                    editor.SelectAllFromHook();
                    break;
                case Keys.V:
                    editor.PasteClipboardFromHook();
                    break;
                case Keys.C:
                    editor.CopySelectionFromHook(cut: false);
                    break;
                case Keys.X:
                    editor.CopySelectionFromHook(cut: true);
                    break;
                case Keys.S:
                    editor.SaveCurrentPresetFromHook();
                    break;
                case Keys.Back:
                    editor.InsertTextFromHook(1, "");
                    break;
            }
        }

        private void RouteEditorHangulDiff(HelperEditorWindow editor, string nextText)
        {
            int common = 0;
            int minLength = Math.Min(_editorLastInjected.Length, nextText.Length);
            while (common < minLength && _editorLastInjected[common] == nextText[common])
                common++;

            int backspaceCount = _editorLastInjected.Length - common;
            string textToAdd = common < nextText.Length ? nextText[common..] : "";
            editor.InsertTextFromHook(backspaceCount, textToAdd);
            _editorLastInjected = nextText;
        }

        private void FlushEditorHangulComposition(HelperEditorWindow editor)
        {
            if (!_editorHangulEngine.IsComposing() && _editorLastInjected.Length == 0)
                return;

            _editorHangulEngine.Flush();
            RouteEditorHangulDiff(editor, _editorHangulEngine.GetCurrentText());
            ResetEditorForwardedText();
        }

        private void ResetEditorForwardedText()
        {
            _editorHangulEngine.Clear();
            _editorLastInjected = "";
        }

        private static bool IsPhysicalKeyDown(Keys key)
        {
            return (GetAsyncKeyState((int)key) & 0x8000) != 0;
        }

        private static bool IsEditorAltKey(uint vkCode)
        {
            return vkCode is (uint)Keys.Menu or (uint)Keys.LMenu or (uint)Keys.RMenu;
        }

        private static bool IsEditorModifierKey(uint vkCode)
        {
            return vkCode is (uint)Keys.ShiftKey or (uint)Keys.LShiftKey or (uint)Keys.RShiftKey
                or (uint)Keys.ControlKey or (uint)Keys.LControlKey or (uint)Keys.RControlKey;
        }

        private static bool IsEditorInputCandidate(uint vkCode, bool ctrl)
        {
            if (vkCode == (uint)Keys.HangulMode)
                return true;

            if (ctrl)
                return vkCode is (uint)Keys.A or (uint)Keys.C or (uint)Keys.V or (uint)Keys.X or (uint)Keys.S or (uint)Keys.Back;

            return (vkCode >= (uint)Keys.A && vkCode <= (uint)Keys.Z)
                || (vkCode >= (uint)Keys.D0 && vkCode <= (uint)Keys.D9)
                || (vkCode >= (uint)Keys.NumPad0 && vkCode <= (uint)Keys.NumPad9)
                || vkCode is (uint)Keys.Space or (uint)Keys.Back or (uint)Keys.Delete or (uint)Keys.Enter
                    or (uint)Keys.Left or (uint)Keys.Right or (uint)Keys.Home or (uint)Keys.End
                    or (uint)Keys.OemMinus or (uint)Keys.Oemplus or (uint)Keys.Oemcomma or (uint)Keys.OemPeriod
                    or (uint)Keys.OemQuestion or (uint)Keys.OemSemicolon or (uint)Keys.OemQuotes
                    or (uint)Keys.OemOpenBrackets or (uint)Keys.OemCloseBrackets or (uint)Keys.OemPipe or (uint)Keys.Oemtilde;
        }

        private static bool TryGetEditorEditingKey(uint vkCode, out string? key)
        {
            key = (Keys)vkCode switch
            {
                Keys.Back => "Backspace",
                Keys.Delete => "Delete",
                Keys.Enter => "Enter",
                Keys.Left => "ArrowLeft",
                Keys.Right => "ArrowRight",
                Keys.Home => "Home",
                Keys.End => "End",
                _ => null
            };

            return key != null;
        }

        private static bool TryGetEditorTextForKey(uint vkCode, bool shift, out string? text)
        {
            text = null;
            Keys key = (Keys)vkCode;

            if (key >= Keys.A && key <= Keys.Z)
            {
                char c = (char)('a' + (key - Keys.A));
                text = shift ? char.ToUpperInvariant(c).ToString() : c.ToString();
                return true;
            }

            if (key >= Keys.D0 && key <= Keys.D9)
            {
                string normal = ((char)('0' + (key - Keys.D0))).ToString();
                string shifted = key switch
                {
                    Keys.D1 => "!",
                    Keys.D2 => "@",
                    Keys.D3 => "#",
                    Keys.D4 => "$",
                    Keys.D5 => "%",
                    Keys.D6 => "^",
                    Keys.D7 => "&",
                    Keys.D8 => "*",
                    Keys.D9 => "(",
                    Keys.D0 => ")",
                    _ => normal
                };
                text = shift ? shifted : normal;
                return true;
            }

            if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
            {
                text = ((char)('0' + (key - Keys.NumPad0))).ToString();
                return true;
            }

            text = key switch
            {
                Keys.Space => " ",
                Keys.OemMinus => shift ? "_" : "-",
                Keys.Oemplus => shift ? "+" : "=",
                Keys.Oemcomma => shift ? "<" : ",",
                Keys.OemPeriod => shift ? ">" : ".",
                Keys.OemQuestion => shift ? "?" : "/",
                Keys.OemSemicolon => shift ? ":" : ";",
                Keys.OemQuotes => shift ? "\"" : "'",
                Keys.OemOpenBrackets => shift ? "{" : "[",
                Keys.OemCloseBrackets => shift ? "}" : "]",
                Keys.OemPipe => shift ? "|" : "\\",
                Keys.Oemtilde => shift ? "~" : "`",
                _ => null
            };

            return text != null;
        }
        private void ToggleCrosshairEnabled()
        {
            if (_crosshairToggleKey == 0)
                return;

            // 단축키 토글은 제작 창의 사용 체크박스와 같은 설정값을 뒤집어 저장한다.
            _crosshairSettings = _crosshairSettings.Normalized();
            _crosshairSettings.Enabled = !_crosshairSettings.Enabled;
            SaveCrosshairSettings();
            UpdateCrosshairOverlay();
            SendSettingsToWeb();
        }

        private void TogglePresetOverlay()
        {
            if (_presetOverlayRequestedVisible || _presetOverlayForm is { IsDisposed: false, Visible: true })
            {
                _presetOverlayRequestedVisible = false;
                _presetOverlayForm?.Hide();
                return;
            }

            if (!IsGameActive())
                return;

            _presetOverlayRequestedVisible = true;
            _helperEditorWindowRequestedVisible = false;
            ShowPresetOverlay();
        }

        private void ShowPresetOverlay()
        {
            if (_presetOverlayKey == 0 || !IsGameActive())
                return;

            if (_presetOverlayForm is { IsDisposed: false, Visible: true })
                return;

            // 포커스 감시 타이머가 반복 호출하므로, 실제로 새로 띄울 때만 프리셋 카드를 다시 만든다.
            var stratagemPresets = LoadPresetSummaries();
            var equipmentPresets = LoadEquipmentPresetSummaries();
            if (stratagemPresets.Count == 0 && equipmentPresets.Count == 0)
            {
                _presetOverlayRequestedVisible = false;
                return;
            }

            HideHelperEditorWindowForExclusivePresetWindow();

            if (_presetOverlayForm == null || _presetOverlayForm.IsDisposed)
            {
                // 게임 중에도 프리셋을 고를 수 있도록 별도 오버레이 창에서 작은 이미지 카드로 보여준다.
                _presetOverlayForm = new PresetOverlayForm(
                    ApplyStratagemPresetFromOverlay,
                    ApplyEquipmentPresetFromOverlay,
                    () => _presetOverlayRequestedVisible = false);
                _presetOverlayForm.FormClosed += (_, _) =>
                {
                    _presetOverlayForm = null;
                    _presetOverlayRequestedVisible = false;
                };
            }
            _presetOverlayForm.UpdatePresets(stratagemPresets, equipmentPresets, _selectedPresetId, _selectedEquipmentPresetId);
            if (!_presetOverlayForm.Visible)
            {
                _presetOverlayForm.Show();
                ApplyCaptureExclusion(_presetOverlayForm);
                _presetOverlayForm.EnsureTopMost();
            }
        }

        private void ToggleHelperEditorWindow()
        {
            if (_helperEditorWindowRequestedVisible || _helperEditorWindow is { IsDisposed: false, Visible: true })
            {
                _helperEditorWindowRequestedVisible = false;
                _helperEditorWindow?.Hide();
                return;
            }

            if (!IsGameActive())
                return;

            _helperEditorWindowRequestedVisible = true;
            _presetOverlayRequestedVisible = false;
            ShowHelperEditorWindow();
        }

        private void ShowHelperEditorWindow()
        {
            if (!IsGameActive())
                return;

            HidePresetOverlayForExclusiveEditorWindow();

            if (_helperEditorWindow == null || _helperEditorWindow.IsDisposed)
            {
                // 창 열기 단축키는 원본 MainForm을 숨기지 않고, 같은 UI를 가진 별도 편집창만 토글한다.
                _helperEditorWindow = new HelperEditorWindow(this);
                _helperEditorWindow.FormClosed += (_, _) =>
                {
                    _helperEditorWindow = null;
                    _helperEditorWindowRequestedVisible = false;
                };
            }

            if (!_helperEditorWindow.Visible)
            {
                _helperEditorWindow.Show();
                ApplyCaptureExclusion(_helperEditorWindow);
                _helperEditorWindow.WindowState = FormWindowState.Normal;
                _helperEditorWindow.EnsureTopMost();
                RestoreGameFocus();
            }
        }

        private void HidePresetOverlayForExclusiveEditorWindow()
        {
            _presetOverlayRequestedVisible = false;
            if (_presetOverlayForm is { IsDisposed: false, Visible: true })
            {
                // F3 편집창과 F4 프리셋 전환창은 입력 대상이 겹치지 않도록 동시에 띄우지 않는다.
                _presetOverlayForm.Hide();
            }
        }

        private void HideHelperEditorWindowForExclusivePresetWindow()
        {
            _helperEditorWindowRequestedVisible = false;
            if (_helperEditorWindow is { IsDisposed: false, Visible: true })
            {
                // F4 프리셋 전환창을 열 때는 F3 편집창을 먼저 닫아 입력 대상과 메뉴전환 상태가 꼬이지 않게 한다.
                _helperEditorWindow.Hide();
            }
        }

        private void UpdateFocusBoundHelperWindows()
        {
            bool gameActive = IsGameActive();
            if (!gameActive)
            {
                // 조준점처럼 포커스가 풀리면 모든 보조창을 숨기고 주입한 보기 키도 반드시 해제한다.
                ReleaseOverlayStratagemViewKey("게임 포커스 상실");
                _overlayForm?.Hide();
                _helperEditorWindow?.Hide();
                _presetOverlayForm?.Hide();
                return;
            }

            if (_helperEditorWindowRequestedVisible)
            {
                ShowHelperEditorWindow();
                return;
            }

            if (_presetOverlayRequestedVisible)
                ShowPresetOverlay();
        }

        internal void NotifyHelperEditorWindowClosedByUser()
        {
            // 포커스 이탈로 숨긴 경우와 사용자가 닫은 경우를 구분해, 닫기 직후 자동 복원되지 않게 한다.
            _helperEditorWindowRequestedVisible = false;
        }
        private List<PresetSummary> LoadPresetSummaries()
        {
            var presets = new List<PresetSummary>();

            using var doc = JsonDocument.Parse(LoadPresetsJson());
            JsonElement presetElements = GetStratagemPresetsElement(doc.RootElement);
            if (presetElements.ValueKind != JsonValueKind.Array)
                return presets;

            foreach (var presetElement in presetElements.EnumerateArray())
            {
                string id = presetElement.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? "" : "";
                string name = presetElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                    continue;

                var loadout = PresetLoadout.FromJson(presetElement.TryGetProperty("loadout", out var loadoutElement) ? loadoutElement : default);
                var supportWeapon = ReadPresetSupportWeaponSettings(presetElement, _supportWeaponSettings);
                var overlaySlots = presetElement.TryGetProperty("overlaySlots", out var overlaySlotsElement)
                    ? ReadOverlaySlotVisibility(overlaySlotsElement)
                    : CreateDefaultOverlaySlotVisibility();
                string equipmentPresetId = presetElement.TryGetProperty("equipmentPresetId", out var equipmentPresetIdElement)
                    ? equipmentPresetIdElement.GetString() ?? ""
                    : "";
                // 기존 프리셋에는 연결값이 없으므로 빈 문자열로 읽어 현재 장비 선택값을 그대로 유지한다.
                presets.Add(new PresetSummary(id, name.Trim(), loadout, supportWeapon, overlaySlots, equipmentPresetId, BuildPresetPreviewImages(loadout)));
            }

            return presets;
        }

        private List<EquipmentPresetSummary> LoadEquipmentPresetSummaries()
        {
            var presets = new List<EquipmentPresetSummary>();
            using var doc = JsonDocument.Parse(LoadPresetsJson());
            JsonElement presetElements = GetEquipmentPresetsElement(doc.RootElement);

            // 구 버전 통합 저장본은 장비 프리셋을 화면에서 바로 사용할 수 있게 임시 ID로 분리한다.
            bool legacy = doc.RootElement.ValueKind == JsonValueKind.Array;
            if (legacy) presetElements = doc.RootElement;
            if (presetElements.ValueKind != JsonValueKind.Array) return presets;

            int index = 0;
            foreach (var presetElement in presetElements.EnumerateArray())
            {
                string sourceId = presetElement.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? "" : "";
                string id = legacy ? $"equipment-{sourceId}" : sourceId;
                string name = presetElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                {
                    index++;
                    continue;
                }

                var loadout = PresetLoadout.FromJson(presetElement.TryGetProperty("loadout", out var loadoutElement) ? loadoutElement : default);
                presets.Add(new EquipmentPresetSummary(id, name.Trim(), loadout, BuildEquipmentPresetPreviewImages(loadout)));
                index++;
            }
            return presets;
        }

        private static SupportWeaponAssistSettings ReadPresetSupportWeaponSettings(JsonElement presetElement, SupportWeaponAssistSettings fallback)
        {
            var settings = fallback.Normalized();
            if (presetElement.ValueKind != JsonValueKind.Object
                || !presetElement.TryGetProperty("supportWeapon", out var supportElement)
                || supportElement.ValueKind != JsonValueKind.Object)
            {
                return settings;
            }

            // 오래된 프리셋은 supportWeapon 필드가 없으므로 현재 전역값을 기반으로 프리셋별 값만 덮어쓴다.
            if (TryGetJsonStringIgnoreCase(supportElement, "mode", "Mode", out string? mode) && mode != null)
                settings.Mode = mode;

            if (TryGetJsonBoolIgnoreCase(supportElement, "gaugeVisible", "GaugeVisible", out bool gaugeVisible))
                settings.GaugeVisible = gaugeVisible;

            if (TryGetJsonBoolIgnoreCase(supportElement, "gaugeAlwaysRefresh", "GaugeAlwaysRefresh", out bool gaugeAlwaysRefresh))
                settings.GaugeAlwaysRefresh = gaugeAlwaysRefresh;

            if (TryGetJsonIntIgnoreCase(supportElement, "gaugeOpacity", "GaugeOpacity", out int gaugeOpacity))
                settings.GaugeOpacity = gaugeOpacity;

            if (TryGetJsonIntIgnoreCase(supportElement, "gaugeOffsetX", "GaugeOffsetX", out int gaugeOffsetX))
                settings.GaugeOffsetX = gaugeOffsetX;

            if (TryGetJsonIntIgnoreCase(supportElement, "gaugeOffsetY", "GaugeOffsetY", out int gaugeOffsetY))
                settings.GaugeOffsetY = gaugeOffsetY;

            if (TryGetJsonBoolIgnoreCase(supportElement, "gaugeVerticalMode", "GaugeVerticalMode", out bool gaugeVerticalMode))
                settings.GaugeVerticalMode = gaugeVerticalMode;

            if (TryGetJsonIntIgnoreCase(supportElement, "verticalGaugeOffsetX", "VerticalGaugeOffsetX", out int verticalGaugeOffsetX))
                settings.VerticalGaugeOffsetX = verticalGaugeOffsetX;

            if (TryGetJsonIntIgnoreCase(supportElement, "verticalGaugeOffsetY", "VerticalGaugeOffsetY", out int verticalGaugeOffsetY))
                settings.VerticalGaugeOffsetY = verticalGaugeOffsetY;

            if (TryGetJsonIntIgnoreCase(supportElement, "gaugeWidth", "GaugeWidth", out int gaugeWidth))
                settings.GaugeWidth = gaugeWidth;

            if (TryGetJsonIntIgnoreCase(supportElement, "gaugeHeight", "GaugeHeight", out int gaugeHeight))
                settings.GaugeHeight = gaugeHeight;

            if (TryGetJsonDoubleIgnoreCase(supportElement, "autoFireReleaseSeconds", "AutoFireReleaseSeconds", out double autoFireReleaseSeconds))
                settings.AutoFireReleaseSeconds = autoFireReleaseSeconds;

            return settings.Normalized();
        }

        private static bool TryGetJsonStringIgnoreCase(JsonElement element, string camelName, string pascalName, out string? value)
        {
            value = null;
            if (element.TryGetProperty(camelName, out var camel) && camel.ValueKind == JsonValueKind.String)
            {
                value = camel.GetString();
                return true;
            }

            if (element.TryGetProperty(pascalName, out var pascal) && pascal.ValueKind == JsonValueKind.String)
            {
                value = pascal.GetString();
                return true;
            }

            return false;
        }

        private static bool TryGetJsonIntIgnoreCase(JsonElement element, string camelName, string pascalName, out int value)
        {
            value = 0;
            if (element.TryGetProperty(camelName, out var camel) && camel.TryGetInt32(out value))
                return true;

            if (element.TryGetProperty(pascalName, out var pascal) && pascal.TryGetInt32(out value))
                return true;

            return false;
        }

        private static bool TryGetJsonBoolIgnoreCase(JsonElement element, string camelName, string pascalName, out bool value)
        {
            value = false;
            if (element.TryGetProperty(camelName, out var camel)
                && (camel.ValueKind == JsonValueKind.True || camel.ValueKind == JsonValueKind.False))
            {
                value = camel.GetBoolean();
                return true;
            }

            if (element.TryGetProperty(pascalName, out var pascal)
                && (pascal.ValueKind == JsonValueKind.True || pascal.ValueKind == JsonValueKind.False))
            {
                value = pascal.GetBoolean();
                return true;
            }

            return false;
        }

        private static bool TryGetJsonDoubleIgnoreCase(JsonElement element, string camelName, string pascalName, out double value)
        {
            value = 0;
            if (element.TryGetProperty(camelName, out var camel) && camel.TryGetDouble(out value))
                return true;

            if (element.TryGetProperty(pascalName, out var pascal) && pascal.TryGetDouble(out value))
                return true;

            return false;
        }

        private Image?[] BuildPresetPreviewImages(PresetLoadout loadout)
        {
            var names = loadout.Stratagems.Take(8).Select(name => ("스트라타젬", name)).ToArray();

            return names
                .Select(item => GetLoadoutPreviewImage(item.Item1, item.Item2))
                .ToArray();
        }

        private Image?[] BuildEquipmentPresetPreviewImages(PresetLoadout loadout)
        {
            var names = new[]
            {
                ("방어구", loadout.Armor),
                ("주 무기", loadout.Primary),
                ("보조 무기", loadout.Secondary),
                ("투척 무기", loadout.Grenade)
            };
            return names.Select(item => GetLoadoutPreviewImage(item.Item1, item.Item2)).ToArray();
        }

        private Image? GetLoadoutPreviewImage(string type, string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            string folder = type switch
            {
                "스트라타젬" => "Stratagems",
                "방어구" => "Armors",
                _ => "Weapons"
            };

            string path = Path.Combine(AppContext.BaseDirectory, "images", folder, $"{name}.png");
            if (!File.Exists(path))
                return null;

            using var original = Image.FromFile(path);
            return new Bitmap(original, new Size(42, 42));
        }

        private void ShowStratagemSelectionDebug(
            string? startName,
            double? startImageScore,
            string? startPosition,
            string? arrivalName,
            double? arrivalImageScore,
            string? arrivalPosition,
            string targetName,
            string? targetPosition)
        {
            if (!IsHandleCreated || IsDisposed)
                return;

            BeginInvoke(new Action(() =>
            {
                if (_stratagemSelectionDebugForm == null || _stratagemSelectionDebugForm.IsDisposed)
                    _stratagemSelectionDebugForm = new StratagemSelectionDebugForm();

                // 자동선택 보정용으로 시작 지점과 도착 지점에서 인식한 스트라타젬을 이미지와 이름으로 보여준다.
                using Image? startImage = GetLoadoutPreviewImage("스트라타젬", startName);
                using Image? arrivalImage = GetLoadoutPreviewImage("스트라타젬", arrivalName);
                using Image? targetImage = GetLoadoutPreviewImage("스트라타젬", targetName);
                _stratagemSelectionDebugForm.ShowResult(startName, startImageScore, startPosition, startImage, arrivalName, arrivalImageScore, arrivalPosition, arrivalImage, targetName, targetPosition, targetImage);
                ApplyCaptureExclusion(_stratagemSelectionDebugForm);
            }));
        }

        private void ApplyStratagemPresetFromOverlay(PresetSummary preset)
        {
            _selectedPresetId = preset.Id;
            // 프리셋 오버레이로 바꿀 때도 게임 안에는 직전 슬롯이 남아 있으므로 OCR 실패 시 보조 시작점으로 보관한다.
            _previousStratagemSlots = _currentSlots.ToArray();
            _currentSlots = preset.Loadout.Stratagems
                .Concat(Enumerable.Repeat("", StratagemSlotCount))
                .Take(StratagemSlotCount)
                .Select(name => string.IsNullOrWhiteSpace(name) ? null : name)
                .ToArray();
            _overlaySlotVisibility = preset.OverlaySlots.ToArray();
            ApplyPresetSupportWeaponSettings(preset.SupportWeapon);

            // F4에서 스트라타젬 프리셋을 고르면 그 프리셋이 저장한 장비 프리셋도 함께 적용한다.
            var linkedEquipmentPreset = LoadEquipmentPresetSummaries()
                .FirstOrDefault(item => string.Equals(item.Id, preset.EquipmentPresetId, StringComparison.Ordinal));
            if (linkedEquipmentPreset != null)
            {
                _selectedEquipmentPresetId = linkedEquipmentPreset.Id;
                _currentLoadoutSlots = new[]
                {
                    EmptyToNull(linkedEquipmentPreset.Loadout.Armor),
                    EmptyToNull(linkedEquipmentPreset.Loadout.Primary),
                    EmptyToNull(linkedEquipmentPreset.Loadout.Secondary),
                    EmptyToNull(linkedEquipmentPreset.Loadout.Grenade)
                };
            }

            SaveSetting();
            SendPresetSelectionToWeb(preset.Id);
            if (linkedEquipmentPreset != null)
                SendEquipmentPresetSelectionToWeb(linkedEquipmentPreset.Id);
            SendCurrentLoadoutToWeb();
            _presetOverlayRequestedVisible = false;
            _presetOverlayForm?.Hide();
            RestoreGameFocus();
        }

        private void ApplyEquipmentPresetFromOverlay(EquipmentPresetSummary preset)
        {
            _selectedEquipmentPresetId = preset.Id;
            _currentLoadoutSlots = new[]
            {
                EmptyToNull(preset.Loadout.Armor),
                EmptyToNull(preset.Loadout.Primary),
                EmptyToNull(preset.Loadout.Secondary),
                EmptyToNull(preset.Loadout.Grenade)
            };
            if (_presetAutoSaveEnabled)
            {
                SavePresetEquipmentLink(_selectedPresetId, preset.Id);
                SendPresetsToWeb();
            }
            SaveSetting();
            SendEquipmentPresetSelectionToWeb(preset.Id);
            SendCurrentLoadoutToWeb();
            _presetOverlayRequestedVisible = false;
            _presetOverlayForm?.Hide();
            RestoreGameFocus();
        }

        private void RestoreGameFocus()
        {
            // 프리셋 선택용 창은 게임 중 보조 UI라서 선택 직후 헬다이버즈2 입력 포커스를 다시 요청한다.
            if (TryGetGameWindow(out IntPtr hwnd))
                SetForegroundWindow(hwnd);
        }

        private void ApplyCaptureExclusion(Form? form)
        {
            if (form == null || form.IsDisposed || !form.IsHandleCreated)
                return;

            // WGC 기반 캡처에서 헬퍼 오버레이가 게임 화면 대신 잡히지 않도록 선택적으로 제외한다.
            uint affinity = _excludeOverlaysFromCapture ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE;
            SetWindowDisplayAffinity(form.Handle, affinity);
        }

        private void ApplyCaptureExclusionToHelperWindows()
        {
            // 옵션을 바꾸면 이미 떠 있는 보조 창 전체에 즉시 반영한다.
            ApplyCaptureExclusion(this);
            ApplyCaptureExclusion(_overlayForm);
            ApplyCaptureExclusion(_presetOverlayForm);
            ApplyCaptureExclusion(_helperEditorWindow);
            ApplyCaptureExclusion(_ocrDebugOverlayForm);
            ApplyCaptureExclusion(_ocrRegionOverlayForm);
            ApplyCaptureExclusion(_stratagemSelectionDebugForm);
            ApplyCaptureExclusion(_crosshairForm);
            ApplyCaptureExclusion(_supportWeaponGaugeForm);
            ApplyCaptureExclusion(_crosshairEditorForm);
            ApplyCaptureExclusion(_ocrRegionSettingsForm);
        }

        private static string? EmptyToNull(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private void SendPresetSelectionToWeb(string id)
        {
            var payload = new
            {
                type = "SELECT_PRESET_FROM_APP",
                id
            };

            // 작은 전환창에서 고른 프리셋도 원본/편집창 UI의 선택 탭에 동시에 반영한다.
            PostWebMessageToViews(payload);
        }

        private void SendEquipmentPresetSelectionToWeb(string id)
        {
            var payload = new
            {
                type = "SELECT_EQUIPMENT_PRESET_FROM_APP",
                id
            };

            PostWebMessageToViews(payload);
        }

        private void ShowOcrDebugOverlay(string targetType, string rawText, string? matchedName, double similarity)
        {
            if (!IsHandleCreated || IsDisposed)
                return;

            BeginInvoke(new Action(() =>
            {
                if (_ocrDebugOverlayForm == null || _ocrDebugOverlayForm.IsDisposed)
                    _ocrDebugOverlayForm = new OcrDebugOverlayForm();

                // 자동선택 중 OCR이 읽은 문자열과 실제 DB 매칭명을 짧게 보여줘 오인식 지점을 확인한다.
                _ocrDebugOverlayForm.ShowResult(targetType, rawText, matchedName, similarity);
                ApplyCaptureExclusion(_ocrDebugOverlayForm);
            }));
        }

        private void ShowOcrRegionOverlay(Rectangle region, int borderThickness, int? durationMs = null)
        {
            if (!IsHandleCreated || IsDisposed)
                return;

            BeginInvoke(new Action(() =>
            {
                if (_ocrRegionOverlayForm == null || _ocrRegionOverlayForm.IsDisposed)
                    _ocrRegionOverlayForm = new OcrRegionOverlayForm();

                // OCR 영역 설정창에서 조정 중인 범위를 게임 화면 위에 붉은 실선으로 미리 보여준다.
                _ocrRegionOverlayForm.ShowRegion(region, borderThickness, durationMs);
                ApplyCaptureExclusion(_ocrRegionOverlayForm);
            }));
        }

        private void HideOcrRegionOverlay()
        {
            if (!IsHandleCreated || IsDisposed)
                return;

            BeginInvoke(new Action(() => _ocrRegionOverlayForm?.Hide()));
        }

        private void PreviewOcrRegion(string targetType, OcrRegionSettings settings)
        {
            if (!TryBuildOcrScreenRegion(settings.Normalized(), out Rectangle region))
                return;

            // 설정창에서 좌표를 조절하는 동안만 현재 선택 항목의 OCR 적용 영역을 계속 표시한다.
            ShowOcrRegionOverlay(region, settings.BorderThickness, null);
        }

        private async Task<string> TestOcrRegionText(string targetType, OcrRegionSettings settings)
        {
            try
            {
                // 설정창 테스트는 자동선택과 같은 OCR 전처리를 써서 실제 작동 때 읽힐 글자에 가깝게 보여준다.
                string text = await ReadOcrTextFromRegion(targetType, settings.Normalized());
                string repairedText = RepairScanText(text);
                return string.IsNullOrWhiteSpace(repairedText) ? "(읽은 글자 없음)" : Regex.Replace(repairedText, @"\s+", " ").Trim();
            }
            catch
            {
                return "(OCR 테스트 실패)";
            }
        }

        private static bool TryGetGameWindow(out IntPtr hwnd)
        {
            hwnd = GetForegroundWindow();
            if (IsHelldiversWindow(hwnd))
                return true;

            IntPtr foundHwnd = IntPtr.Zero;
            EnumWindows((candidate, _) =>
            {
                if (!IsWindowVisible(candidate) || !IsHelldiversWindow(candidate))
                    return true;

                foundHwnd = candidate;
                return false;
            }, IntPtr.Zero);

            hwnd = foundHwnd;
            return hwnd != IntPtr.Zero;
        }

        private static bool IsHelldiversWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            var className = new StringBuilder(256);
            return GetClassName(hwnd, className, className.Capacity) != 0
                && className.ToString() == "stingray_window";
        }

        private static bool TryBuildOcrScreenRegion(OcrRegionSettings settings, out Rectangle region)
        {
            region = Rectangle.Empty;

            if (!TryBuildGameScreenRect(settings.X, settings.Y, settings.Width, settings.Height, out region))
                return false;

            return region.Width > 0 && region.Height > 0;
        }

        private static bool TryBuildGameScreenRect(int designX, int designY, int designWidth, int designHeight, out Rectangle region)
        {
            region = Rectangle.Empty;

            // 설정창이나 다른 보조 창이 포커스를 가져가도 실행 중인 게임 창 기준으로 1920x1080 좌표를 화면 좌표로 변환한다.
            if (!TryGetGameWindow(out IntPtr hwnd)) return false;
            if (!GetClientRect(hwnd, out Rectangle clientRect)) return false;

            double currentAspect = (double)clientRect.Width / clientRect.Height;
            double targetAspect = 16.0 / 9.0;
            double finalRatio;
            double offsetX = 0;
            double offsetY = 0;

            if (currentAspect > targetAspect)
            {
                finalRatio = (double)clientRect.Height / 1080.0;
                offsetX = (clientRect.Width - (1920.0 * finalRatio)) / 2.0;
            }
            else
            {
                finalRatio = (double)clientRect.Width / 1920.0;
                offsetY = (clientRect.Height - (1080.0 * finalRatio)) / 2.0;
            }

            Point startPoint = new Point(0, 0);
            ClientToScreen(hwnd, ref startPoint);

            region = new Rectangle(
                startPoint.X + (int)Math.Round(designX * finalRatio + offsetX),
                startPoint.Y + (int)Math.Round(designY * finalRatio + offsetY),
                (int)Math.Round(designWidth * finalRatio),
                (int)Math.Round(designHeight * finalRatio)
            );

            return region.Width > 0 && region.Height > 0;
        }

        private async Task<AutoReloadDetectionResult> DetectReloadPromptAsync(AutoReloadSettings? overrideSettings = null)
        {
            AutoReloadSettings settings = (overrideSettings ?? _autoReloadSettings).Normalized();
            if (!TryBuildGameScreenRect(settings.X, settings.Y, settings.Width, settings.Height, out Rectangle region))
                return AutoReloadDetectionResult.Empty with { Note = "게임 창 또는 감지 영역을 찾지 못했습니다." };

            try
            {
                var ocrSettings = new OcrRegionSettings
                {
                    X = settings.X,
                    Y = settings.Y,
                    Width = settings.Width,
                    Height = settings.Height,
                    BorderThickness = settings.BorderThickness
                };
                string rawText = await ReadOcrTextFromRegion("자동재장전", ocrSettings);
                string scanText = CleanScanText(rawText);
                int matchedKeywords = 0;

                // 한국어 UI의 "무기 장전"을 기본으로 보고, 영어 UI의 RELOAD도 같은 의미로 처리한다.
                if (scanText.Contains("무기", StringComparison.Ordinal)) matchedKeywords++;
                if (scanText.Contains("장전", StringComparison.Ordinal)) matchedKeywords++;
                if (scanText.Contains("RELOAD", StringComparison.Ordinal)) matchedKeywords += 2;

                bool isReloadPrompt = matchedKeywords >= settings.MinimumPromptMatches;
                string note = isReloadPrompt ? "중앙 무기 장전 안내 확인" : "중앙 장전 안내를 찾지 못했습니다.";
                return new AutoReloadDetectionResult(isReloadPrompt, matchedKeywords, settings.MinimumPromptMatches, rawText, region, note);
            }
            catch
            {
                return AutoReloadDetectionResult.Empty with { Note = "화면 캡처에 실패했습니다." };
            }
        }

        private static bool TryGetGameClientRegion(out Rectangle region)
        {
            region = Rectangle.Empty;

            if (!TryGetGameWindow(out IntPtr hwnd)) return false;

            if (!GetClientRect(hwnd, out Rectangle clientRect) || clientRect.Width <= 0 || clientRect.Height <= 0)
                return false;

            Point topLeft = new Point(0, 0);
            ClientToScreen(hwnd, ref topLeft);
            region = new Rectangle(topLeft.X, topLeft.Y, clientRect.Width, clientRect.Height);
            return true;
        }

        private OcrEngine? CreateKoreanOcrEngine()
        {
            // 헬다이버즈2 한국어 UI 이름은 한글을 기본으로 읽고, 장비 코드용 영어/숫자는 ko-KR OCR의 보조 인식에 맡긴다.
            return OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(KoreanOcrLanguageTag));
        }

        private async Task<string> RecognizeTextFromBitmap(Bitmap bitmap)
        {
            await _ocrRecognitionGate.WaitAsync();
            try
            {
                using var ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Png);
                ms.Position = 0;

                using var ras = ms.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(ras);
                using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

                if (_ocrEngine == null)
                    _ocrEngine = CreateKoreanOcrEngine();

                if (_ocrEngine == null)
                    return "";

                var ocrResult = await _ocrEngine.RecognizeAsync(softwareBitmap);
                return NormalizeOcrRawText(ocrResult.Text ?? "");
            }
            finally
            {
                _ocrRecognitionGate.Release();
            }
        }

        private async Task<string> ReadOcrTextFromRegion(string targetType, OcrRegionSettings ocrSettings)
        {
            if (!TryBuildOcrScreenRegion(ocrSettings.Normalized(), out Rectangle region))
                return "";

            using Bitmap cap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(cap))
            {
                g.CopyFromScreen(region.Left, region.Top, 0, 0, cap.Size);
            }

            // 자동선택 OCR과 같은 전처리로 흰 UI 글자를 검출해 테스트 결과와 실제 판독 결과를 맞춘다.
            double scale = targetType == "스트라타젬" ? 4.5 : 3.5;
            double radius = targetType == "스트라타젬" ? 1.8 : 3.95;
            int pad = 90;

            int resizedW = (int)Math.Round(cap.Width * scale);
            int resizedH = (int)Math.Round(cap.Height * scale);
            int limit = (int)Math.Ceiling(radius);

            using Bitmap resized = new Bitmap(resizedW, resizedH, PixelFormat.Format32bppArgb);
            using (Graphics rg = Graphics.FromImage(resized))
            {
                rg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                rg.DrawImage(cap, 0, 0, resized.Width, resized.Height);
            }

            var offsets = new List<(int dx, int dy)>();
            for (int ky = -limit; ky <= limit; ky++)
            {
                for (int kx = -limit; kx <= limit; kx++)
                {
                    if (Math.Sqrt(kx * kx + ky * ky) <= radius)
                        offsets.Add((kx, ky));
                }
            }

            int finalW = resized.Width + (pad * 2);
            int finalH = resized.Height + (pad * 2);
            BitmapData? srcData = null;
            BitmapData? dstData = null;

            using Bitmap finalBmp = new Bitmap(finalW, finalH, PixelFormat.Format32bppArgb);
            try
            {
                srcData = resized.LockBits(new Rectangle(0, 0, resized.Width, resized.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                dstData = finalBmp.LockBits(new Rectangle(0, 0, finalW, finalH), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                int srcStride = Math.Abs(srcData.Stride);
                int dstStride = Math.Abs(dstData.Stride);
                byte[] srcPixels = new byte[srcStride * srcData.Height];
                byte[] dstPixels = new byte[dstStride * dstData.Height];

                Marshal.Copy(srcData.Scan0, srcPixels, 0, srcPixels.Length);
                Array.Fill<byte>(dstPixels, 255);

                for (int y = 0; y < resized.Height; y++)
                {
                    for (int x = 0; x < resized.Width; x++)
                    {
                        int srcIdx = (y * srcStride) + (x * 4);
                        if (srcPixels[srcIdx + 0] > 165 && srcPixels[srcIdx + 1] > 165 && srcPixels[srcIdx + 2] > 165)
                        {
                            foreach (var (dx, dy) in offsets)
                            {
                                int outX = x + pad + dx;
                                int outY = y + pad + dy;
                                if (outX >= 0 && outX < finalW && outY >= 0 && outY < finalH)
                                {
                                    int dstIdx = (outY * dstStride) + (outX * 4);
                                    dstPixels[dstIdx + 0] = 0;
                                    dstPixels[dstIdx + 1] = 0;
                                    dstPixels[dstIdx + 2] = 0;
                                    dstPixels[dstIdx + 3] = 255;
                                }
                            }
                        }
                    }
                }

                Marshal.Copy(dstPixels, 0, dstData.Scan0, dstPixels.Length);
            }
            finally
            {
                if (srcData != null)
                    resized.UnlockBits(srcData);

                if (dstData != null)
                    finalBmp.UnlockBits(dstData);
            }

            return await RecognizeTextFromBitmap(finalBmp);
        }

        private static string CleanScanText(string input)
        {
            return Regex.Replace(RepairScanText(input), @"[^가-힣a-zA-Z0-9]", "").ToUpperInvariant();
        }
        private static double CalculateOcrNameSimilarity(string cleanOcr, string cleanDb, string targetType)
        {
            if (string.IsNullOrWhiteSpace(cleanOcr) || string.IsNullOrWhiteSpace(cleanDb))
                return 0.0;

            if (targetType == "스트라타젬" && cleanOcr.Contains(cleanDb))
            {
                // 게임 풀네임 안에 DB 등록명이 일부로 들어가면 우선 매칭하고, 긴 이름일수록 동명이인 후보에서 유리하게 한다.
                return 1.0 + Math.Min(0.1, cleanDb.Length / 100.0);
            }

            if (cleanOcr.Contains(cleanDb) || cleanDb.Contains(cleanOcr))
                return 1.0;

            double directScore = GetNormalizedEditSimilarity(cleanOcr, cleanDb);
            double partialScore = GetBestPartialSimilarity(cleanOcr, cleanDb);
            string ocrJamo = ToHangulJamoSearchText(cleanOcr);
            string dbJamo = ToHangulJamoSearchText(cleanDb);
            double jamoDirectScore = GetNormalizedEditSimilarity(ocrJamo, dbJamo);
            double jamoPartialScore = GetBestPartialSimilarity(ocrJamo, dbJamo);

            // OCR이 모음/받침만 틀리는 경우를 살리기 위해 원문 글자 점수와 자모 점수를 섞고, 긴 OCR 문장에서는 부분구간 점수를 우선 반영한다.
            double textScore = Math.Max(directScore, partialScore);
            double jamoScore = Math.Max(jamoDirectScore, jamoPartialScore);
            double blendedScore = (textScore * 0.62) + (jamoScore * 0.38);
            return Math.Clamp(Math.Max(textScore, blendedScore), 0.0, 1.1);
        }

        private static double GetNormalizedEditSimilarity(string source, string target)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target))
                return string.Equals(source, target, StringComparison.Ordinal) ? 1.0 : 0.0;

            int distance = GetLevenshteinDistanceStatic(source, target);
            return 1.0 - ((double)distance / Math.Max(source.Length, target.Length));
        }

        private static double GetBestPartialSimilarity(string source, string target)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target))
                return 0.0;

            if (source.Length <= target.Length + 2)
                return GetNormalizedEditSimilarity(source, target);

            double best = 0.0;
            int minLength = Math.Max(1, target.Length - 2);
            int maxLength = Math.Min(source.Length, target.Length + 2);
            for (int length = minLength; length <= maxLength; length++)
            {
                for (int start = 0; start <= source.Length - length; start++)
                {
                    string window = source.Substring(start, length);
                    double score = GetNormalizedEditSimilarity(window, target);
                    if (score > best)
                        best = score;
                }
            }

            return best;
        }

        private static string ToHangulJamoSearchText(string input)
        {
            var builder = new StringBuilder(input.Length * 3);
            foreach (char c in input)
                AppendHangulJamo(builder, c);
            return builder.ToString();
        }

        private static void AppendHangulJamo(StringBuilder builder, char c)
        {
            const int hangulBase = 0xAC00;
            const int hangulEnd = 0xD7A3;
            string choseong = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";
            string jungseong = "ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ";
            string jongseong = "\0ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ";

            if (c < hangulBase || c > hangulEnd)
            {
                builder.Append(c);
                return;
            }

            int syllable = c - hangulBase;
            int cho = syllable / (21 * 28);
            int jung = (syllable % (21 * 28)) / 28;
            int jong = syllable % 28;
            builder.Append(choseong[cho]);
            builder.Append(jungseong[jung]);
            if (jong > 0)
                builder.Append(jongseong[jong]);
        }

        private static string NormalizeOcrRawText(string input)
        {
            // OCR 원문은 한국어 UI 기준으로 한글/영어/숫자와 장비 코드 구분 문자만 보존해 한자/기호 오인식을 줄인다.
            string allowedOnly = Regex.Replace(input, @"[^가-힣a-zA-Z0-9\s/.\-]", " ");
            return Regex.Replace(allowedOnly, @"\s+", " ").Trim();
        }

        private static string RepairScanText(string input)
        {
            string repaired = input
                .Replace(")(", "X").Replace("卜", "I").Replace("ⅹ", "X")
                .Replace("ⅴ", "V").Replace("+", "B").Replace("l", "I")
                .Replace("불", "블").Replace("뱸", "뱀").Replace("르", "드")
                .Replace("엔", "맨").Replace("앤", "맨").Replace("멘", "맨")
                .Replace("책", "잭").Replace("피", "퍼").Replace("저", "처")
                .Replace("쳐", "처").Replace("적", "척").Replace("셀", "샐")
                .Replace("로비", "로버").Replace("위프 팩", "워프 팩").Replace("눠-", "워프 팩").Replace("눠", "워프 팩")
                .Replace("개년", "캐넌").Replace("근 커 포드", "로켓 포드").Replace("근커포드", "로켓포드")
                .Replace("일", "열").Replace("진", "친").Replace("제", "체")
                .Replace("장", "창").Replace("04", "CM").Replace("21", "기")
                .Replace("G-23", "23").Replace("I", "1").Replace("O", "0");

            // 한국어 이름 끝의 '프 팩'이 OCR에서 '- 0nH'나 '- 0iH'처럼 라틴/숫자 조합으로 깨지는 경우를 보정한다.
            repaired = Regex.Replace(repaired, @"워\s*[-ㅡ]?\s*[0oO]?\s*[ni1l]\s*H", "워프 팩", RegexOptions.IgnoreCase);
            repaired = Regex.Replace(repaired, @"프\s*[0oO]?\s*[ni1l]\s*H", "프 팩", RegexOptions.IgnoreCase);
            return repaired;
        }

        private static int GetLevenshteinDistanceStatic(string s, string t)
        {
            if (string.IsNullOrEmpty(s)) return string.IsNullOrEmpty(t) ? 0 : t.Length;
            if (string.IsNullOrEmpty(t)) return s.Length;

            int[,] d = new int[s.Length + 1, t.Length + 1];
            for (int i = 0; i <= s.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= t.Length; j++) d[0, j] = j;

            for (int i = 1; i <= s.Length; i++)
            {
                for (int j = 1; j <= t.Length; j++)
                {
                    int cost = t[j - 1] == s[i - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }

            return d[s.Length, t.Length];
        }


        private string? MatchStratagemIconFromScreen()
        {
            try
            {
                if (!TryBuildGameScreenRect(55, 390, 430, 650, out Rectangle searchRegion))
                {
                    _lastIconMatchDebugLine = "region=(none), selectedSlot=(none), best=(none), score=0.000, matched=(null), note=region-failed";
                    return null;
                }

                using Bitmap capture = new(searchRegion.Width, searchRegion.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(capture))
                {
                    g.CopyFromScreen(searchRegion.Left, searchRegion.Top, 0, 0, capture.Size);
                }

                List<Rectangle> slotCandidates = FindSelectedSlotCandidates(capture, out int yellowComponentCount);
                Rectangle? selectedSlot = ChooseSelectedSlotCandidate(slotCandidates);
                if (!selectedSlot.HasValue)
                {
                    _lastIconMatchDebugLine = $"region={FormatRectangle(searchRegion)}, yellowComponents={yellowComponentCount}, candidates=0, selectedSlot=(none), best=(none), score=0.000, matched=(null), note=slot-not-found";
                    SaveStratagemIconMatchDebug(capture, searchRegion, slotCandidates, null, null, null, 0.0, null, "slot-not-found", yellowComponentCount);
                    return null;
                }

                // 조각난 노란 선택 테두리를 먼저 하나의 슬롯 사각형으로 묶고, 실제 비교는 슬롯 안쪽 정사각형 crop으로 수행한다.
                int insetX = Math.Max(10, selectedSlot.Value.Width / 7);
                int insetY = Math.Max(10, selectedSlot.Value.Height / 7);
                Rectangle inner = Rectangle.Intersect(Rectangle.Inflate(selectedSlot.Value, -insetX, -insetY), new Rectangle(Point.Empty, capture.Size));
                if (inner.Width <= 0 || inner.Height <= 0)
                {
                    _lastIconMatchDebugLine = $"region={FormatRectangle(searchRegion)}, yellowComponents={yellowComponentCount}, candidates={slotCandidates.Count}, selectedSlot={FormatNullableRectangle(selectedSlot)}, best=(none), score=0.000, matched=(null), note=inner-empty";
                    SaveStratagemIconMatchDebug(capture, searchRegion, slotCandidates, selectedSlot, null, null, 0.0, null, "inner-empty", yellowComponentCount);
                    return null;
                }

                using Bitmap crop = capture.Clone(inner, PixelFormat.Format32bppArgb);
                using Bitmap screenIcon = ResizeBitmapForIconMatch(crop, 64);

                string? bestName = null;
                double bestScore = 0.0;
                foreach (var item in _parsedData.Where(d => d.Type == "스트라타젬" && d.Category != "임무" && d.Category != "패시브" && !_disabledItems.Contains(d.Name)))
                {
                    Image? templateImage = GetStratagemImage(item.Name);
                    if (templateImage == null)
                        continue;

                    using Bitmap templateIcon = ResizeBitmapForIconMatch(templateImage, 64);
                    double score = CompareIconBitmaps(screenIcon, templateIcon);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestName = item.Name;
                    }
                }

                // 아이콘 매칭은 OCR 실패 시 보조 수단이라 낮은 점수로 보정 이동을 만들면 인접 스트라타젬 오선택으로 이어질 수 있다.
                string? matchedName = bestScore >= StratagemIconFallbackMinScore ? bestName : null;
                Rectangle absoluteSlot = new(searchRegion.Left + selectedSlot.Value.Left, searchRegion.Top + selectedSlot.Value.Top, selectedSlot.Value.Width, selectedSlot.Value.Height);
                Rectangle absoluteInner = new(searchRegion.Left + inner.Left, searchRegion.Top + inner.Top, inner.Width, inner.Height);
                _lastIconMatchDebugLine = $"region={FormatRectangle(searchRegion)}, yellowComponents={yellowComponentCount}, candidates={slotCandidates.Count}, selectedSlot={FormatRectangle(absoluteSlot)}, iconCrop={FormatRectangle(absoluteInner)}, best={(bestName ?? "(none)")}, score={bestScore:0.000}, matched={(matchedName ?? "(null)")}";
                SaveStratagemIconMatchDebug(capture, searchRegion, slotCandidates, selectedSlot, inner, bestName, bestScore, matchedName, "matched", yellowComponentCount);
                return matchedName;
            }
            catch (Exception ex)
            {
                _lastIconMatchDebugLine = $"region=(unknown), selectedSlot=(unknown), best=(none), score=0.000, matched=(null), note=exception:{ex.GetType().Name}";
                return null;
            }
        }
        private static Bitmap ResizeBitmapForIconMatch(Image source, int size)
        {
            Bitmap resized = new(size, size, PixelFormat.Format32bppArgb);
            using Graphics g = Graphics.FromImage(resized);
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(source, 0, 0, size, size);
            return resized;
        }

        private static double CompareIconBitmaps(Bitmap screenIcon, Bitmap templateIcon)
        {
            double bestScore = 0.0;

            // 아이콘 crop이 1~2픽셀 흔들려도 맞는 후보를 고를 수 있게 작은 위치 보정 중 최고점을 사용한다.
            for (int offsetY = -3; offsetY <= 3; offsetY++)
            {
                for (int offsetX = -3; offsetX <= 3; offsetX++)
                {
                    double score = CompareIconBitmapsAtOffset(screenIcon, templateIcon, offsetX, offsetY);
                    if (score > bestScore)
                        bestScore = score;
                }
            }

            return bestScore;
        }

        private static double CompareIconBitmapsAtOffset(Bitmap screenIcon, Bitmap templateIcon, int offsetX, int offsetY)
        {
            int truePositive = 0;
            int falsePositive = 0;
            int falseNegative = 0;
            double colorBonus = 0.0;

            for (int y = 0; y < templateIcon.Height; y++)
            {
                int screenY = y + offsetY;
                if (screenY < 0 || screenY >= screenIcon.Height)
                    continue;

                for (int x = 0; x < templateIcon.Width; x++)
                {
                    int screenX = x + offsetX;
                    if (screenX < 0 || screenX >= screenIcon.Width)
                        continue;

                    Color template = templateIcon.GetPixel(x, y);
                    bool templateHasIcon = IsTemplateIconPixel(template);
                    if (!templateHasIcon)
                        continue;

                    Color screen = screenIcon.GetPixel(screenX, screenY);
                    bool screenHasIcon = IsScreenIconMatchPixel(screen);
                    if (screenHasIcon)
                    {
                        truePositive++;
                        int colorDistance = Math.Abs(screen.R - template.R) + Math.Abs(screen.G - template.G) + Math.Abs(screen.B - template.B);
                        colorBonus += Math.Max(0.0, 1.0 - (colorDistance / 765.0));
                    }
                    else
                    {
                        falseNegative++;
                    }
                }
            }

            for (int y = 0; y < screenIcon.Height; y++)
            {
                int templateY = y - offsetY;
                if (templateY < 0 || templateY >= templateIcon.Height)
                    continue;

                for (int x = 0; x < screenIcon.Width; x++)
                {
                    int templateX = x - offsetX;
                    if (templateX < 0 || templateX >= templateIcon.Width)
                        continue;

                    bool screenHasIcon = IsScreenIconMatchPixel(screenIcon.GetPixel(x, y));
                    bool templateHasIcon = IsTemplateIconPixel(templateIcon.GetPixel(templateX, templateY));
                    if (screenHasIcon && !templateHasIcon)
                        falsePositive++;
                }
            }

            int denominator = truePositive + falsePositive + falseNegative;
            if (denominator == 0)
                return 0.0;

            double shapeScore = (double)truePositive / denominator;
            double averageColor = truePositive > 0 ? colorBonus / truePositive : 0.0;
            return Math.Clamp((shapeScore * 0.82) + (averageColor * 0.18), 0.0, 1.0);
        }

        private static bool IsScreenIconMatchPixel(Color c)
        {
            if (IsSelectionYellow(c) || IsSlotFramePixel(c))
                return false;

            // 화면 아이콘은 배경과 선택 테두리가 섞일 수 있어 색상 자체보다 밝은/채도 높은 아이콘 픽셀 여부를 본다.
            return TryClassifyEquippedSlotContentPixel(c, out _);
        }

        private static bool IsTemplateIconPixel(Color c)
        {
            if (c.A < 40)
                return false;

            int max = Math.Max(c.R, Math.Max(c.G, c.B));
            int min = Math.Min(c.R, Math.Min(c.G, c.B));
            int saturation = max - min;
            return max >= 70 && (saturation >= 25 || min >= 120);
        }

        private async Task<string?> MatchItemFromScreen(string targetType)
        {
            try
            {
                OcrRegionSettings ocrSettings = _ocrRegionSettings.TryGetValue(targetType, out var savedSettings)
                    ? savedSettings.Normalized()
                    : OcrRegionSettings.DefaultFor(targetType);

                if (!TryBuildOcrScreenRegion(ocrSettings, out Rectangle region))
                {
                    _lastOcrMatchDebugLine = $"type={targetType}, region=(none), raw=(none), candidates=(none), best=(none), score=0.000, matched=(null), note=region-failed";
                    return null;
                }

                using (Bitmap cap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(cap))
                    {
                        g.CopyFromScreen(region.Left, region.Top, 0, 0, cap.Size);
                    }

                    // 스트라타젬 이름은 영문 코드와 한글이 섞여 있어 과한 굵게 처리가 글자를 뭉개지 않도록 별도 값을 쓴다.
                    double scale = targetType == "스트라타젬" ? 4.5 : 3.5;
                    double radius = targetType == "스트라타젬" ? 1.8 : 3.95;
                    int pad = 90;

                    int resizedW = (int)Math.Round(cap.Width * scale);
                    int resizedH = (int)Math.Round(cap.Height * scale);
                    int limit = (int)Math.Ceiling(radius);

                    using (Bitmap resized = new Bitmap(resizedW, resizedH, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics rg = Graphics.FromImage(resized))
                        {
                            rg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            rg.DrawImage(cap, 0, 0, resized.Width, resized.Height);
                        }

                        var offsets = new List<(int dx, int dy)>();
                        for (int ky = -limit; ky <= limit; ky++)
                        {
                            for (int kx = -limit; kx <= limit; kx++)
                            {
                                if (Math.Sqrt(kx * kx + ky * ky) <= radius)
                                    offsets.Add((kx, ky));
                            }
                        }

                        int finalW = resized.Width + (pad * 2);
                        int finalH = resized.Height + (pad * 2);

                        BitmapData? srcData = null;
                        BitmapData? dstData = null;

                        using (Bitmap finalBmp = new Bitmap(finalW, finalH, PixelFormat.Format32bppArgb))
                        {
                            try
                            {
                                srcData = resized.LockBits(new Rectangle(0, 0, resized.Width, resized.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                                dstData = finalBmp.LockBits(new Rectangle(0, 0, finalW, finalH), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                                int srcStride = Math.Abs(srcData.Stride);
                                int dstStride = Math.Abs(dstData.Stride);

                                byte[] srcPixels = new byte[srcStride * srcData.Height];
                                byte[] dstPixels = new byte[dstStride * dstData.Height];

                                Marshal.Copy(srcData.Scan0, srcPixels, 0, srcPixels.Length);
                                Array.Fill<byte>(dstPixels, 255);

                                for (int y = 0; y < resized.Height; y++)
                                {
                                    for (int x = 0; x < resized.Width; x++)
                                    {
                                        int srcIdx = (y * srcStride) + (x * 4);
                                        if (srcPixels[srcIdx + 0] > 165 && srcPixels[srcIdx + 1] > 165 && srcPixels[srcIdx + 2] > 165)
                                        {
                                            foreach (var (dx, dy) in offsets)
                                            {
                                                int outX = x + pad + dx;
                                                int outY = y + pad + dy;
                                                if (outX >= 0 && outX < finalW && outY >= 0 && outY < finalH)
                                                {
                                                    int dstIdx = (outY * dstStride) + (outX * 4);
                                                    dstPixels[dstIdx + 0] = 0;
                                                    dstPixels[dstIdx + 1] = 0;
                                                    dstPixels[dstIdx + 2] = 0;
                                                    dstPixels[dstIdx + 3] = 255;
                                                }
                                            }
                                        }
                                    }
                                }

                                Marshal.Copy(dstPixels, 0, dstData.Scan0, dstPixels.Length);
                            }
                            finally
                            {
                                if (srcData != null)
                                    resized.UnlockBits(srcData);

                                if (dstData != null)
                                    finalBmp.UnlockBits(dstData);
                            }

                            string rawText = await RecognizeTextFromBitmap(finalBmp);
                            IEnumerable<string> GetOcrNameCandidates(string input)
                            {
                                string repaired = RepairScanText(NormalizeOcrRawText(input)).Trim();
                                var candidates = new List<string> { CleanScanText(repaired) };

                                // 게임 표시명 앞의 장비 코드(StA-X3, APW-1 등)는 DB명에 없으므로 코드 토큰만 제거한 후보도 비교한다.
                                string withoutCode = Regex.Replace(repaired, @"^\s*[A-Za-z]{1,5}[A-Za-z0-9/.-]{1,10}\s+", "");
                                string cleanWithoutCode = CleanScanText(withoutCode);
                                if (!string.IsNullOrEmpty(cleanWithoutCode))
                                    candidates.Add(cleanWithoutCode);

                                return candidates
                                    .Where(candidate => !string.IsNullOrEmpty(candidate))
                                    .Distinct();
                            }

                            var cleanOcrCandidates = GetOcrNameCandidates(rawText).ToArray();
                            string compactRawText = Regex.Replace(rawText ?? "", @"\s+", " ").Trim();
                            string compactCandidates = cleanOcrCandidates.Length == 0
                                ? "(none)"
                                : string.Join(" / ", cleanOcrCandidates.Select(candidate => candidate.Length > 80 ? candidate[..80] + "..." : candidate));

                            if (cleanOcrCandidates.Length == 0)
                            {
                                // OCR 원문이 비었는지, 정제 과정에서 후보가 사라졌는지 다음 실패 기록에서 바로 볼 수 있게 남긴다.
                                _lastOcrMatchDebugLine = $"type={targetType}, region=x={region.X},y={region.Y},w={region.Width},h={region.Height}, raw={(string.IsNullOrEmpty(compactRawText) ? "(empty)" : compactRawText)}, candidates=(none), best=(none), score=0.000, matched=(null), note=no-candidates";
                                return null;
                            }

                            var matchResult = _parsedData
                                .Where(x => x.Type == targetType)
                                .Select(x => {
                                    string cleanDB = CleanScanText(x.Name);
                                    double sim = cleanOcrCandidates
                                        .Select(cleanOCR => CalculateOcrNameSimilarity(cleanOCR, cleanDB, targetType))
                                        .DefaultIfEmpty(0.0)
                                        .Max();
                                    return new { Item = x, Similarity = sim };
                                })
                                .OrderByDescending(x => x.Similarity)
                                .FirstOrDefault();

                            /*
                            string folderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug");
                            Directory.CreateDirectory(folderPath);
                            string debugPath = Path.Combine(folderPath, $"debug_{DateTime.Now:HHmmss}.png");
                            finalBmp.Save(debugPath, ImageFormat.Png);

                            string debugInfo = $"[OCR 인식 결과]: {rawText}\n" + $"[정제된 결과]: {cleanOCR}\n";
                            if (matchResult.Similarity != 1.0)
                            {
                                debugInfo += $"[가장 유사한 아이템]: {matchResult.Item.Name}\n" +
                                             $"[유사도]: {matchResult.Similarity:P1} (기준: 60%)\n" +
                                             $"[결과]: {(matchResult.Similarity > 0.6 ? "매칭 성공" : "매칭 실패 (유사도 낮음)")}";
                            }
                            else debugInfo = "유사도 완벽 일치";
                            Debug.WriteLine(debugInfo);
                            */

                            string? matchedName = matchResult != null && matchResult.Similarity > 0.6 ? matchResult.Item.Name : null;
                            string bestName = matchResult?.Item.Name ?? "(none)";
                            double bestScore = matchResult?.Similarity ?? 0.0;
                            _lastOcrMatchDebugLine = $"type={targetType}, region=x={region.X},y={region.Y},w={region.Width},h={region.Height}, raw={(string.IsNullOrEmpty(compactRawText) ? "(empty)" : compactRawText)}, candidates={compactCandidates}, best={bestName}, score={bestScore:0.000}, matched={(matchedName ?? "(null)")}";

                            if (matchedName != null)
                            {
                                return matchedName;
                            }
                            return null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _lastOcrMatchDebugLine = $"type={targetType}, region=(unknown), raw=(none), candidates=(none), best=(none), score=0.000, matched=(null), note=exception:{ex.GetType().Name}";
                return null;
            }
        }

        private int GetLevenshteinDistance(string s, string t)
        {
            if (string.IsNullOrEmpty(s)) return string.IsNullOrEmpty(t) ? 0 : t.Length;
            if (string.IsNullOrEmpty(t)) return s.Length;
            int n = s.Length;
            int m = t.Length;
            int[,] d = new int[n + 1, m + 1];
            for (int i = 0; i <= n; d[i, 0] = i++) ;
            for (int j = 0; j <= m; d[0, j] = j++) ;
            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }
            return d[n, m];
        }

        private void TriggerAutoSelection()
        {
            if (!IsGameActive() || _isChat)
                return;

            if (Interlocked.Exchange(ref _isSending, 1) == 1)
                return;

            var cts = new CancellationTokenSource();
            _autoSelectionCts = cts;

            Task.Run(async () =>
            {
                try
                {
                    // 자동선택 중 같은 자동선택 단축키를 다시 누르면 이 토큰이 취소되어 이후 키 입력/대기 지점에서 즉시 빠져나온다.
                    await RunAutoSelection();
                }
                catch (OperationCanceledException)
                {
                    // 사용자가 자동선택 단축키를 다시 눌러 강제 중지한 경우에는 조용히 종료한다.
                }
                finally
                {
                    if (ReferenceEquals(_autoSelectionCts, cts))
                        _autoSelectionCts = null;

                    cts.Dispose();
                    Interlocked.Exchange(ref _isSending, 0);
                }
            });
        }
        private Task TestModeStepAsync(string message, bool delay = true)
        {
            // 테스트모드는 더 이상 자동선택에 인위적인 대기나 화면 로그를 추가하지 않고, 진단 파일 저장 여부만 제어한다.
            return Task.CompletedTask;
        }

        private async Task RunAutoSelection()
        {
            await TestModeStepAsync("자동선택 시작");
            if (_currentLoadoutSlots.Any(s => !string.IsNullOrEmpty(s)))
            {
                await TestModeStepAsync("장비 메뉴 열기");
                await TapKey(Keys.R);
                await Task.Delay(100);

                int gearR = 0, gearC = 0;
                var gearLayout = new Dictionary<int, (int R, int C)>
                {
                    { 0, (0, 1) }, // 방어구
                    { 1, (1, 0) }, // 주 무기
                    { 2, (1, 1) }, // 보조 무기
                    { 3, (1, 2) }  // 투척 무기
                };

                for (int i = 0; i <= 3; i++)
                {
                    if (!string.IsNullOrEmpty(_currentLoadoutSlots[i]))
                    {
                        var targetSlot = gearLayout[i];

                        while (gearR < targetSlot.R) { await TapKey(Keys.S); gearR++; }
                        while (gearR > targetSlot.R) { await TapKey(Keys.W); gearR--; }

                        while (gearC < targetSlot.C) { await TapKey(Keys.D); gearC++; }
                        while (gearC > targetSlot.C) { await TapKey(Keys.A); gearC--; }

                        await TestModeStepAsync($"{_currentLoadoutSlots[i]} 장비 선택창 열기");
                        await TapKey(Keys.Space);
                        await ExecuteAutoSelection(i);

                        await TestModeStepAsync("장비 선택창 닫기");
                        await TapKey(Keys.Escape);
                        await Task.Delay(100);
                    }
                }

                await TestModeStepAsync("장비 메뉴 닫기");
                await TapKey(Keys.R);
                await Task.Delay(100);
            }

            if (_currentSlots.Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => _parsedData.FirstOrDefault(d => d.Name == name))
                .Any(data => !string.IsNullOrEmpty(data.Name) && data.Category != "임무"))
            {
                await TestModeStepAsync("스트라타젬 선택창 열기");
                await TapKey(Keys.Space);
                await ExecuteAutoSelection(4);
            }
            await TestModeStepAsync("자동선택 종료", delay: false);
        }

        private async Task ExecuteAutoSelection(int index)
        {
            string autoDebugRunId = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var autoDebugLines = new List<string>();
            void LogAutoSelectionDebug(string message)
            {
                if (!_testModeEnabled) return;

                // 테스트모드가 켜진 경우에만 목표 좌표, 시작 좌표, 키 입력 흐름을 파일로 남긴다.
                autoDebugLines.Add($"{DateTime.Now:HH:mm:ss.fff} {message}");
                SaveAutoSelectionDebugInfo(autoDebugRunId, autoDebugLines);
            }

            void CaptureAutoSelectionDebug(string label, bool important = false)
            {
                if (!_testModeEnabled)
                    return;

                string? path = SaveAutoSelectionScreenCapture(autoDebugRunId, label);
                LogAutoSelectionDebug($"screenshot.{label}={path ?? "(capture failed)"}");
            }

            int GetMenuMotionSettleDelay(int minimumMs = 0)
            {
                return Math.Max(Math.Max(0, _inputDelay), minimumMs);
            }

            async Task WaitForMenuMotionSettle(string reason, int minimumMs = 0)
            {
                int delayMs = GetMenuMotionSettleDelay(minimumMs);
                if (delayMs > 0)
                    await Task.Delay(delayMs);

                // 스크롤이 발생한 직후에는 다음 방향/선택 입력이 씹힐 수 있어 필요할 때만 최소 안정화 시간을 강제한다.
                LogAutoSelectionDebug($"menuMotionSettle.{reason}={delayMs}ms");
            }

            async Task WaitAfterMoveBeforeArrivalRead(string reason)
            {
                int delayMs = Math.Max(0, _inputDelay);
                if (delayMs > 0)
                    await Task.Delay(delayMs);

                // 마지막 이동키가 게임 UI에 반영되기 전에 OCR을 시작하지 않도록 설정된 입력 딜레이만큼 맞춰 기다린다.
                LogAutoSelectionDebug($"arrivalReadDelay.{reason}={delayMs}ms");
            }

            void LogLastOcrDetail(string label)
            {
                if (!_testModeEnabled || string.IsNullOrWhiteSpace(_lastOcrMatchDebugLine))
                    return;

                // OCR 실패는 원문/후보/최고점수를 같이 봐야 원인 추적이 되므로 테스트모드 로그에 마지막 OCR 상세값을 붙인다.
                LogAutoSelectionDebug($"{label}: {_lastOcrMatchDebugLine}");
            }

            void LogLastIconDetail(string label)
            {
                if (!_testModeEnabled || string.IsNullOrWhiteSpace(_lastIconMatchDebugLine))
                    return;

                // 아이콘 판독은 OCR 실패 시에만 보조로 쓰이므로, 선택 영역과 최고 점수를 따로 남긴다.
                LogAutoSelectionDebug($"{label}: {_lastIconMatchDebugLine}");
            }

            async Task<string?> MatchStratagemNameWithIconFallback(string detailLabel)
            {
                string? name = await MatchItemFromScreen("스트라타젬");
                LogLastOcrDetail($"{detailLabel}.ocrDetail");
                if (name != null)
                    return name;

                string? iconName = MatchStratagemIconFromScreen();
                LogLastIconDetail($"{detailLabel}.iconDetail");
                return iconName;
            }

            async Task MoveToTarget((int Group, int Row, int Col) target, int curG, int curR, int curC, int totalTabs, int colCount, List<int> groupItemCounts, List<string>? keyLog = null, bool pressSelect = true)
            {
                async Task PressMoveKey(Keys key, string reason)
                {
                    keyLog?.Add($"{key}:{reason}");
                    await TapKey(key);
                }

                bool movedGroups = false;
                while (curG != target.Group)
                {
                    int diff = target.Group - curG;
                    if (diff > totalTabs / 2 || (diff < 0 && diff >= -totalTabs / 2))
                    {
                        await PressMoveKey(Keys.Z, $"group {curG}->{(curG - 1 + totalTabs) % totalTabs}");
                        curG = (curG - 1 + totalTabs) % totalTabs;
                    }
                    else
                    {
                        await PressMoveKey(Keys.C, $"group {curG}->{(curG + 1) % totalTabs}");
                        curG = (curG + 1) % totalTabs;
                    }

                    // 카테고리 탭을 바꾸면 게임 커서는 첫 줄로만 이동하고, 같은 줄 안의 열 위치는 유지된다.
                    curR = 0;
                    movedGroups = true;
                }

                if (movedGroups)
                    // Z/C 탭 전환도 위아래 스크롤과 같은 최소 안정화 시간을 줘 첫 후속 입력이 씹히지 않게 한다.
                    await WaitForMenuMotionSettle("after-group-change", minimumMs: 150);
                else
                    await Task.Delay(Math.Max(20, _inputDelay));

                bool movedRows = false;
                while (curR != target.Row)
                {
                    int nextR = (curR < target.Row) ? curR + 1 : curR - 1;
                    if ((nextR * colCount) + curC >= groupItemCounts[curG])
                    {
                        // 현재 열이 다음 행에 없을 때만 첫 열로 이동한다. 평소에는 선택된 스트라타젬 좌표를 그대로 시작점으로 쓴다.
                        while (curC > 0) { await PressMoveKey(Keys.A, $"missing-column reset {curC}->{curC - 1}"); curC--; }
                    }

                    await PressMoveKey(curR < target.Row ? Keys.S : Keys.W, $"row {curR}->{nextR}");
                    curR = nextR;
                    movedRows = true;
                }

                if (movedRows)
                    await WaitForMenuMotionSettle("after-row-scroll", minimumMs: 150);
                else
                    await Task.Delay(Math.Max(20, _inputDelay));

                while (curC < target.Col) { await PressMoveKey(Keys.D, $"col {curC}->{curC + 1}"); curC++; }
                while (curC > target.Col) { await PressMoveKey(Keys.A, $"col {curC}->{curC - 1}"); curC--; }

                if (pressSelect)
                    await PressMoveKey(Keys.Space, "select");
            }

            async Task<bool> WaitForStratagemSelectionMenuReady()
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    if (await IsStratagemSelectionMenuOpen())
                        return true;

                    await Task.Delay(75);
                }

                // 스트라타젬 선택창이 뜨기 전 준비화면을 상단 슬롯 검사로 오판하지 않도록 준비되지 않으면 중단한다.
                LogAutoSelectionDebug("abort=stratagem-menu-ready-timeout");
                CaptureAutoSelectionDebug("abort-stratagem-menu-ready-timeout", important: true);
                await TestModeStepAsync("스트라타젬 선택창 확인 실패: 자동선택 중단", delay: false);
                return false;
            }

            var (type, colCount) = index switch
            {
                0 => ("방어구", 3),
                1 => ("주 무기", 2),
                2 => ("보조 무기", 2),
                3 => ("투척 무기", 3),
                _ => ("스트라타젬", 4)
            };

            void BuildSelectionMap(
                List<IGrouping<string, (string Type, string Category, string Name)>> categories,
                out Dictionary<string, (int Group, int Row, int Col)> map,
                out List<int> itemCounts)
            {
                map = new Dictionary<string, (int Group, int Row, int Col)>();
                itemCounts = new List<int>();

                for (int g = 0; g < categories.Count; g++)
                {
                    var items = categories[g].ToList();

                    int targetIndex = items.FindIndex(d => d.Name == "B-01 전술");
                    if (targetIndex != -1)
                    {
                        var original = items[targetIndex];
                        for (int j = 3; j >= 1; j--)
                        {
                            items.Insert(targetIndex + 1, (original.Type, original.Category, $"더미데이터{j}"));
                        }
                    }

                    itemCounts.Add(items.Count);

                    for (int i = 0; i < items.Count; i++)
                    {
                        map[items[i].Name] = (g, i / colCount, i % colCount);
                    }
                }
            }

            // 스트라타젬은 제외 목록이 실제 게임 선택창에서 없는 항목이라는 뜻이므로 좌표 계산에서도 제외한다.
            bool useGameOrderForCurrentType = type == "스트라타젬";
            var groupedCategories = BuildAutoSelectionCategories(type, useGameStratagemOrder: useGameOrderForCurrentType, includeDisabledStratagems: false);
            BuildSelectionMap(groupedCategories, out var itemMap, out var groupItemCounts);
            int totalTabs = groupedCategories.Count;
            int coordinateOnlyStartGroup = groupedCategories.FindIndex(group => group.Key == "공격");
            if (coordinateOnlyStartGroup < 0)
                coordinateOnlyStartGroup = 0;

            int curG = 0, curR = 0, curC = 0;

            LogAutoSelectionDebug($"type={type}, index={index}, colCount={colCount}, totalTabs={totalTabs}");
            LogAutoSelectionDebug($"groupCounts={string.Join(", ", groupedCategories.Select((group, groupIndex) => $"{groupIndex}:{group.Key}={group.Count()}"))}");

            if (index >= 0 && index <= 3)
            {
                string? targetName = _currentLoadoutSlots[index];
                if (string.IsNullOrEmpty(targetName) || !itemMap.TryGetValue(targetName, out var target))
                    return;

                await TestModeStepAsync($"{type} 현재 항목 인식");
                string? currentWeaponName = null;
                for (int retry = 0; retry < 3; retry++)
                {
                    currentWeaponName = await MatchItemFromScreen(type);
                    if (currentWeaponName != null) break;
                    if (retry < 2)
                        await Task.Delay(10);
                }

                if (currentWeaponName != null && itemMap.TryGetValue(currentWeaponName, out var current))
                {
                    curG = current.Group;
                    curR = current.Row;
                    curC = current.Col;

                    await TestModeStepAsync($"{type} 목표 이동: {targetName}", delay: false);
                    await MoveToTarget(target, curG, curR, curC, totalTabs, colCount, groupItemCounts);
                    await TestModeStepAsync($"{type} 선택 완료");
                }
            }
            else if (index == 4)
            {
                var selectedItems = _currentSlots
                     .Select((name, slotIndex) => new { Name = name, SlotIndex = slotIndex })
                     .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                     .Where(item => itemMap.ContainsKey(item.Name!))
                     .Where(item =>
                     {
                         var data = _parsedData.FirstOrDefault(d => d.Name == item.Name);
                         return !string.IsNullOrEmpty(data.Name) && data.Category != "임무" && !_disabledItems.Contains(data.Name);
                     })
                     .Take(4)
                     .ToList();

                if (selectedItems.Count == 0)
                    return;

                bool stratagemReselectEnabled = _stratagemReselectEnabled;

                string DescribeStratagemTarget(string? name, int slotIndex)
                {
                    var data = _parsedData.FirstOrDefault(d => d.Name == name);
                    string category = string.IsNullOrWhiteSpace(data.Category) ? "(unknown)" : data.Category;
                    string position = itemMap.TryGetValue(name ?? "", out var pos)
                        ? $"G{pos.Group}/R{pos.Row}/C{pos.Col}"
                        : "(missing-position)";
                    string sequence = _sequenceMap.TryGetValue(name ?? "", out var seq)
                        ? string.Join(",", seq)
                        : "(no-sequence)";

                    // 프리셋 목표 정보는 나중에 실패 로그만 봐도 DB 분류, 좌표, 입력 조합을 같이 비교할 수 있게 남긴다.
                    return $"slot={slotIndex + 1}, name={name}, category={category}, pos={position}, sequence={sequence}";
                }

                LogAutoSelectionDebug($"presetTargets={string.Join(" | ", selectedItems.Select(item => DescribeStratagemTarget(item.Name, item.SlotIndex)))}");
                LogAutoSelectionDebug($"stratagemReselectEnabled={stratagemReselectEnabled}");
                bool isMenuOpen = true;
                int prepSlotIndex = 0;
                bool currentPositionInitialized = !stratagemReselectEnabled;
                if (!stratagemReselectEnabled)
                {
                    // 재선택 OFF의 좌표 모드는 게임 선택창 최초 커서가 공격 탭의 첫 번째 사용 가능 항목이라고 보고 시작한다.
                    curG = coordinateOnlyStartGroup;
                    curR = 0;
                    curC = 0;
                    LogAutoSelectionDebug($"coordinateOnlyStart=G{curG}/R{curR}/C{curC}, category={groupedCategories[curG].Key}");
                }

                if (stratagemReselectEnabled)
                {
                    if (!await WaitForStratagemSelectionMenuReady())
                        return;
                }
                else
                {
                    // 재선택 OFF도 첫 진입 타이밍은 ON과 동일하게 선택창 준비 확인을 기다린다.
                    // 이후 이동/선택은 OCR 보정 없이 프리셋 좌표만 사용한다.
                    if (!await WaitForStratagemSelectionMenuReady())
                        return;

                    // 메뉴 준비 확인 직후 UI 반영만 짧게 기다리고, 이후에는 좌표 전용으로 바로 이동한다.
                    const int coordinateOnlyPostReadyDelayMs = 150;
                    await Task.Delay(coordinateOnlyPostReadyDelayMs);
                    LogAutoSelectionDebug($"coordinateOnlyMenuReadyWaitedLikeReselect=true, postReadyDelay={coordinateOnlyPostReadyDelayMs}ms");
                }

                CaptureAutoSelectionDebug("stratagem-menu-ready");

                for (int i = 0; i < selectedItems.Count; i++)
                {
                    var selected = selectedItems[i];
                    var target = itemMap[selected.Name!];
                    var targetData = _parsedData.FirstOrDefault(d => d.Name == selected.Name);
                    bool slotWasEquipped = false;
                    LogAutoSelectionDebug($"target[{i}] {DescribeStratagemTarget(selected.Name, selected.SlotIndex)}, targetType={targetData.Type}");

                    if (!isMenuOpen)
                    {
                        // 준비화면에서는 현재 스트라타젬 슬롯 포커스에서 목표 슬롯까지 이동한 뒤 선택창을 연다.
                        while (prepSlotIndex < selected.SlotIndex) { await TapKey(Keys.D); LogAutoSelectionDebug($"prepSlotKey=D, slot {prepSlotIndex + 1}->{prepSlotIndex + 2}"); prepSlotIndex++; }
                        while (prepSlotIndex > selected.SlotIndex) { await TapKey(Keys.A); LogAutoSelectionDebug($"prepSlotKey=A, slot {prepSlotIndex + 1}->{prepSlotIndex}"); prepSlotIndex--; }

                        await TestModeStepAsync($"슬롯 {selected.SlotIndex + 1} 선택창 열기");
                        await TapKey(Keys.Space);
                        LogAutoSelectionDebug($"prepSlotKey=Space, openSlot={selected.SlotIndex + 1}");
                        if (!await WaitForStratagemSelectionMenuReady())
                            return;

                        CaptureAutoSelectionDebug($"slot-{selected.SlotIndex + 1}-opened");
                        currentPositionInitialized = !stratagemReselectEnabled;
                        if (!stratagemReselectEnabled)
                        {
                            // 준비화면에서 슬롯을 다시 열 때도 게임 커서는 공격 탭 첫 칸에서 시작한다고 본다.
                            curG = coordinateOnlyStartGroup;
                            curR = 0;
                            curC = 0;
                        }
                    }

                    if (stratagemReselectEnabled && IsSelectedEquippedStratagemSlotOccupied())
                    {
                        slotWasEquipped = true;
                        LogAutoSelectionDebug($"equippedSlot.detected slot={selected.SlotIndex + 1}");
                        string? equippedName = null;
                        for (int retry = 0; retry < 3; retry++)
                        {
                            equippedName = await MatchStratagemNameWithIconFallback($"equippedSlot.slot-{selected.SlotIndex + 1}");
                            if (equippedName != null)
                                break;

                            if (retry < 2)
                                await Task.Delay(25);
                        }

                        if (equippedName == null)
                        {
                            // 이미 채워진 슬롯은 현재 장착 항목을 시작점으로 써야 하므로, 이름을 못 읽으면 오교체 방지를 위해 멈춘다.
                            LogAutoSelectionDebug($"abort=equipped-slot-ocr-null, slot={selected.SlotIndex + 1}, expected={selected.Name}");
                            CaptureAutoSelectionDebug($"abort-equipped-slot-ocr-null-slot-{selected.SlotIndex + 1}", important: true);
                            await TestModeStepAsync("장착 슬롯 OCR 실패: 자동선택 중단", delay: false);
                            return;
                        }

                        if (string.Equals(equippedName, selected.Name, StringComparison.Ordinal))
                        {
                            // 목표 스트라타젬이 이미 들어 있는 슬롯은 선택창을 닫고 다음 슬롯으로 넘어간다.
                            LogAutoSelectionDebug($"equippedSlot.alreadyTarget slot={selected.SlotIndex + 1}, name={equippedName}");
                            await TestModeStepAsync($"이미 목표 장착됨: {equippedName}", delay: false);
                            await TapKey(Keys.Escape);
                            isMenuOpen = false;
                            prepSlotIndex = selected.SlotIndex;
                            currentPositionInitialized = false;
                            continue;
                        }

                        if (itemMap.TryGetValue(equippedName, out var equippedPosition))
                        {
                            // 이미 다른 스트라타젬이 들어 있으면 현재 장착 항목의 좌표를 시작점으로 삼아 목표까지 교체 이동한다.
                            curG = equippedPosition.Group;
                            curR = equippedPosition.Row;
                            curC = equippedPosition.Col;
                            currentPositionInitialized = true;
                            LogAutoSelectionDebug($"equippedSlot.replaceStart slot={selected.SlotIndex + 1}, current={equippedName}, currentPos=G{curG}/R{curR}/C{curC}, target={selected.Name}, targetPos=G{target.Group}/R{target.Row}/C{target.Col}");
                        }
                        else
                        {
                            LogAutoSelectionDebug($"abort=equipped-slot-map-missing, slot={selected.SlotIndex + 1}, current={equippedName}, expected={selected.Name}");
                            CaptureAutoSelectionDebug($"abort-equipped-slot-map-missing-slot-{selected.SlotIndex + 1}", important: true);
                            await TestModeStepAsync($"장착 항목 좌표 없음: {equippedName}", delay: false);
                            return;
                        }
                    }

                    if (stratagemReselectEnabled && !currentPositionInitialized)
                    {
                        string? currentName = await MatchStratagemNameWithIconFallback("startPosition");
                        if (currentName != null && itemMap.TryGetValue(currentName, out var current))
                        {
                            // 아이콘 매칭은 쓰지 않지만, 선택창이 기억한 카테고리/칸을 보정하려고 이름 OCR만 시작 좌표에 반영한다.
                            curG = current.Group;
                            curR = current.Row;
                            curC = current.Col;
                            currentPositionInitialized = true;
                            LogAutoSelectionDebug($"startPosition.ocr={currentName}, pos=G{curG}/R{curR}/C{curC}");
                        }
                        else
                        {
                            currentPositionInitialized = true;
                            LogAutoSelectionDebug($"startPosition.fallback=G{curG}/R{curR}/C{curC}, ocr={(currentName ?? "(null)")}");
                        }
                    }

                    CaptureAutoSelectionDebug($"before-move-slot-{selected.SlotIndex + 1}");
                    var keyLog = new List<string>();
                    await TestModeStepAsync($"목표 이동: {selected.Name}", delay: false);
                    LogAutoSelectionDebug($"moveStart=G{curG}/R{curR}/C{curC}, moveTarget=G{target.Group}/R{target.Row}/C{target.Col}");
                    await MoveToTarget(target, curG, curR, curC, totalTabs, colCount, groupItemCounts, keyLog, pressSelect: false);
                    LogAutoSelectionDebug($"moveKeys={string.Join(" > ", keyLog)}");

                    string? arrivalName = null;
                    bool arrivalMatchesTarget = !stratagemReselectEnabled;
                    if (stratagemReselectEnabled)
                    {
                        await WaitAfterMoveBeforeArrivalRead($"slot-{selected.SlotIndex + 1}");
                        CaptureAutoSelectionDebug($"arrival-before-select-slot-{selected.SlotIndex + 1}");
                        for (int retry = 0; retry < 3; retry++)
                        {
                            arrivalName = await MatchStratagemNameWithIconFallback($"arrivalCheck.slot-{selected.SlotIndex + 1}.try-{retry + 1}");
                            if (arrivalName != null)
                                break;

                            if (retry < 2)
                                await Task.Delay(25);
                        }

                        arrivalMatchesTarget = string.Equals(arrivalName, selected.Name, StringComparison.Ordinal);
                        LogAutoSelectionDebug($"arrivalCheck slot={selected.SlotIndex + 1}, expected={selected.Name}, ocr={(arrivalName ?? "(null)")}, match={arrivalMatchesTarget}");

                        if (arrivalName == null)
                        {
                            // 도착 위치까지 계산 이동을 마쳤는데 OCR만 실패한 경우에는 낮은 신뢰도의 아이콘 결과로 보정하지 않고 현재 목표 좌표를 선택한다.
                            // 이번 궤도 가스 타격처럼 상세 패널은 맞지만 OCR이 빈값이 되는 상황에서 불필요한 한 칸 보정을 막기 위한 처리다.
                            LogAutoSelectionDebug($"arrival-ocr-null-assume-target slot={selected.SlotIndex + 1}, expected={selected.Name}, targetPos=G{target.Group}/R{target.Row}/C{target.Col}");
                            CaptureAutoSelectionDebug($"arrival-ocr-null-assume-target-slot-{selected.SlotIndex + 1}", important: true);
                        }

                        if (!arrivalMatchesTarget && arrivalName != null && itemMap.TryGetValue(arrivalName, out var arrivalPosition))
                        {
                            // 빠른 연속 입력에서 마지막 이동이 씹히면 도착 OCR이 목표와 달라진다.
                            // 잘못된 항목을 바로 선택하지 않고, 실제 도착 위치를 시작점으로 삼아 목표까지 한 번 더 보정 이동한다.
                            var correctionKeyLog = new List<string>();
                            LogAutoSelectionDebug($"arrivalCorrection.start current={arrivalName}, currentPos=G{arrivalPosition.Group}/R{arrivalPosition.Row}/C{arrivalPosition.Col}, target={selected.Name}, targetPos=G{target.Group}/R{target.Row}/C{target.Col}");
                            await MoveToTarget(target, arrivalPosition.Group, arrivalPosition.Row, arrivalPosition.Col, totalTabs, colCount, groupItemCounts, correctionKeyLog, pressSelect: false);
                            LogAutoSelectionDebug($"arrivalCorrection.keys={string.Join(" > ", correctionKeyLog)}");

                            await WaitAfterMoveBeforeArrivalRead($"correction-slot-{selected.SlotIndex + 1}");
                            CaptureAutoSelectionDebug($"arrival-after-correction-slot-{selected.SlotIndex + 1}", important: true);
                            arrivalName = await MatchStratagemNameWithIconFallback($"arrivalCorrection.slot-{selected.SlotIndex + 1}");
                            arrivalMatchesTarget = string.Equals(arrivalName, selected.Name, StringComparison.Ordinal);
                            LogAutoSelectionDebug($"arrivalCorrection.result slot={selected.SlotIndex + 1}, expected={selected.Name}, ocr={(arrivalName ?? "(null)")}, match={arrivalMatchesTarget}");
                        }

                        if (!arrivalMatchesTarget && arrivalName != null)
                        {
                            // 목표가 아닌 항목임을 OCR로 확인한 경우에는 오선택을 막기 위해 해당 슬롯 자동선택을 중단한다.
                            LogAutoSelectionDebug($"abort=arrival-mismatch, slot={selected.SlotIndex + 1}, expected={selected.Name}, arrival={arrivalName}");
                            CaptureAutoSelectionDebug($"abort-arrival-mismatch-slot-{selected.SlotIndex + 1}", important: true);
                            await TestModeStepAsync($"도착 항목 불일치: {arrivalName}", delay: false);
                            return;
                        }
                    }
                    else
                    {
                        // 재선택 OFF는 빈 슬롯 최초 선택용 좌표 모드라 OCR/아이콘 확인과 보정 이동을 모두 생략한다.
                        LogAutoSelectionDebug($"coordinateOnlySelect slot={selected.SlotIndex + 1}, expected={selected.Name}, targetPos=G{target.Group}/R{target.Row}/C{target.Col}");
                        CaptureAutoSelectionDebug($"coordinate-only-before-select-slot-{selected.SlotIndex + 1}");
                    }

                    keyLog.Add("Space:select-after-arrival-check");
                    await TapKey(Keys.Space, holdMs: Math.Max(_inputDelay, 120), afterMs: GetMenuMotionSettleDelay());
                    LogAutoSelectionDebug($"selectKey=Space, expected={selected.Name}, selectedByArrival={(arrivalName ?? "(unknown)")}, holdMs={Math.Max(_inputDelay, 120)}, afterMs={GetMenuMotionSettleDelay()}");

                    curG = target.Group;
                    curR = target.Row;
                    curC = target.Col;

                    await Task.Delay(20);
                    CaptureAutoSelectionDebug($"after-select-slot-{selected.SlotIndex + 1}");
                    bool shouldVerifyMenuState = i == selectedItems.Count - 1;
                    // 이미 들어 있던 스트라타젬을 교체하면 게임이 준비화면으로 복귀하므로 다음 슬롯은 준비화면에서 다시 열어야 한다.
                    isMenuOpen = !stratagemReselectEnabled ? true : slotWasEquipped ? false : shouldVerifyMenuState ? await IsStratagemSelectionMenuOpen() : true;
                    LogAutoSelectionDebug($"selectionResult slot={selected.SlotIndex + 1}, expected={selected.Name}, arrival={(arrivalName ?? "(null)")}, arrivalMatch={arrivalMatchesTarget}, accepted={!isMenuOpen}, currentPos=G{curG}/R{curR}/C{curC}");
                    if (!isMenuOpen)
                    {
                        // 선택 후 준비화면으로 돌아온 경우 다음 루프에서 준비화면 기준으로 슬롯을 다시 연다.
                        prepSlotIndex = selected.SlotIndex;
                        currentPositionInitialized = !stratagemReselectEnabled;
                    }
                }
            }
        }

        private static List<IGrouping<string, (string Type, string Category, string Name)>> BuildAutoSelectionCategories(
            string type,
            bool useGameStratagemOrder,
            bool includeDisabledStratagems)
        {
            var items = _parsedData
                .Where(d => d.Type == type && d.Category != "임무" && d.Category != "패시브")
                .Where(d => type != "스트라타젬" || includeDisabledStratagems || !_disabledItems.Contains(d.Name))
                .Where(d => type == "스트라타젬" || !_disabledItems.Contains(d.Name))
                .ToList();

            if (type != "스트라타젬" || !useGameStratagemOrder)
                return items.GroupBy(d => d.Category).ToList();

            string[] gameCategoryOrder = { "보급", "방어", "공격" };
            // 스트라타젬 좌표 계산은 사용자가 제외한 미보유 항목을 빼고, 탭 순서는 게임 시작 탭에 맞춘다.
            return items
                .GroupBy(d => d.Category)
                .OrderBy(group =>
                {
                    int order = Array.IndexOf(gameCategoryOrder, group.Key);
                    return order < 0 ? gameCategoryOrder.Length : order;
                })
                .ToList();
        }

        private static bool TryBuildEquippedStratagemSlotSearchRegion(out Rectangle searchRegion)
        {
            // 이미 장착된 슬롯인지 확인할 때만 위쪽 4개 슬롯 범위를 캡처한다.
            return TryBuildGameScreenRect(45, 250, 360, 150, out searchRegion);
        }

        private static List<Rectangle> BuildSelectedSlotCandidates(List<(Rectangle Rect, int Pixels)> yellowComponents, Bitmap screenCapture, Size searchSize)
        {
            var candidates = new List<Rectangle>();
            const int slotJoinDistance = 92;

            foreach (var anchor in yellowComponents)
            {
                Point anchorCenter = new(anchor.Rect.Left + anchor.Rect.Width / 2, anchor.Rect.Top + anchor.Rect.Height / 2);
                Rectangle union = anchor.Rect;
                int totalPixels = 0;

                foreach (var component in yellowComponents)
                {
                    Point center = new(component.Rect.Left + component.Rect.Width / 2, component.Rect.Top + component.Rect.Height / 2);
                    if (Math.Abs(center.X - anchorCenter.X) > slotJoinDistance || Math.Abs(center.Y - anchorCenter.Y) > slotJoinDistance)
                        continue;

                    union = Rectangle.Union(union, component.Rect);
                    totalPixels += component.Pixels;
                }

                double ratio = (double)Math.Min(union.Width, union.Height) / Math.Max(union.Width, union.Height);
                bool looksLikeSlot =
                    union.Width >= 55 && union.Height >= 55 &&
                    union.Width <= 155 && union.Height <= 155 &&
                    ratio >= 0.62 &&
                    totalPixels >= 80;

                if (!looksLikeSlot)
                    continue;

                Point slotCenter = new(union.Left + union.Width / 2, union.Top + union.Height / 2);
                int slotSide = Math.Clamp(Math.Max(union.Width, union.Height) + 4, 55, 155);
                Rectangle squareSlot = new(slotCenter.X - slotSide / 2, slotCenter.Y - slotSide / 2, slotSide, slotSide);

                Rectangle clippedSquareSlot = Rectangle.Intersect(squareSlot, new Rectangle(Point.Empty, searchSize));
                Rectangle refinedSlot = RefineSlotRegionWithFrame(screenCapture, clippedSquareSlot);

                // 노란 선택 테두리는 점선이라 한 조각씩 끊겨 보이므로, 가까운 노란 조각들을 합친 뒤 주변 회색 슬롯 프레임으로 중심을 보정한다.
                candidates.Add(refinedSlot);
            }

            return candidates
                .DistinctBy(rect => (rect.Left / 4, rect.Top / 4, rect.Width / 4, rect.Height / 4))
                .ToList();
        }

        private static Rectangle RefineSlotRegionWithFrame(Bitmap bitmap, Rectangle approximateSlot)
        {
            Rectangle bitmapBounds = new(Point.Empty, bitmap.Size);
            Rectangle scanBounds = Rectangle.Intersect(Rectangle.Inflate(approximateSlot, 22, 22), bitmapBounds);
            if (scanBounds.Width <= 0 || scanBounds.Height <= 0)
                return approximateSlot;

            Point center = new(approximateSlot.Left + approximateSlot.Width / 2, approximateSlot.Top + approximateSlot.Height / 2);
            int minVerticalScore = Math.Max(8, approximateSlot.Height / 5);
            int minHorizontalScore = Math.Max(8, approximateSlot.Width / 5);

            int? left = FindBestFrameLine(scanBounds.Left, center.X - 18, x => CountFramePixelsOnVertical(bitmap, x, scanBounds.Top, scanBounds.Bottom), minVerticalScore);
            int? right = FindBestFrameLine(center.X + 18, scanBounds.Right - 1, x => CountFramePixelsOnVertical(bitmap, x, scanBounds.Top, scanBounds.Bottom), minVerticalScore);
            int? top = FindBestFrameLine(scanBounds.Top, center.Y - 18, y => CountFramePixelsOnHorizontal(bitmap, y, scanBounds.Left, scanBounds.Right), minHorizontalScore);
            int? bottom = FindBestFrameLine(center.Y + 18, scanBounds.Bottom - 1, y => CountFramePixelsOnHorizontal(bitmap, y, scanBounds.Left, scanBounds.Right), minHorizontalScore);

            if (!left.HasValue || !right.HasValue || !top.HasValue || !bottom.HasValue)
                return approximateSlot;

            Rectangle refined = Rectangle.FromLTRB(left.Value, top.Value, right.Value + 1, bottom.Value + 1);
            double ratio = (double)Math.Min(refined.Width, refined.Height) / Math.Max(refined.Width, refined.Height);
            bool looksLikeSlot = refined.Width >= 55 && refined.Height >= 55 && refined.Width <= 155 && refined.Height <= 155 && ratio >= 0.62;

            // 회색 슬롯 프레임이 충분히 잡힌 프레임에서는 노란 애니메이션 대신 실제 슬롯 외곽을 중심 기준으로 사용한다.
            return looksLikeSlot ? Rectangle.Intersect(refined, bitmapBounds) : approximateSlot;
        }

        private static int? FindBestFrameLine(int start, int end, Func<int, int> scoreAt, int minScore)
        {
            if (start > end)
                return null;

            int bestPosition = start;
            int bestScore = -1;
            for (int position = start; position <= end; position++)
            {
                int score = scoreAt(position);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPosition = position;
                }
            }

            return bestScore >= minScore ? bestPosition : null;
        }

        private static int CountFramePixelsOnVertical(Bitmap bitmap, int x, int top, int bottom)
        {
            int count = 0;
            for (int y = top; y < bottom; y++)
            {
                if (IsSlotFramePixel(bitmap.GetPixel(x, y)))
                    count++;
            }

            return count;
        }

        private static int CountFramePixelsOnHorizontal(Bitmap bitmap, int y, int left, int right)
        {
            int count = 0;
            for (int x = left; x < right; x++)
            {
                if (IsSlotFramePixel(bitmap.GetPixel(x, y)))
                    count++;
            }

            return count;
        }

        private static bool IsSlotFramePixel(Color c)
        {
            if (IsSelectionYellow(c))
                return false;

            int max = Math.Max(c.R, Math.Max(c.G, c.B));
            int min = Math.Min(c.R, Math.Min(c.G, c.B));
            return max >= 55 && max <= 190 && max - min <= 55;
        }

        private static bool IsSelectionYellow(Color c)
        {
            return c.R > 175 && c.G > 145 && c.B < 95 && c.R - c.B > 100;
        }

        private bool IsSelectedEquippedStratagemSlotOccupied()
        {
            if (!TryBuildEquippedStratagemSlotSearchRegion(out Rectangle searchRegion))
                return false;

            using Bitmap cap = new(searchRegion.Width, searchRegion.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(cap))
            {
                g.CopyFromScreen(searchRegion.Left, searchRegion.Top, 0, 0, cap.Size);
            }

            Rectangle? selectedSlot = TryFindSelectedSlotRegion(cap);
            if (!selectedSlot.HasValue)
            {
                SaveEquippedSlotDetectionDebug(cap, searchRegion, null, Rectangle.Empty, 0, 0, false, new Dictionary<string, int>(), new Dictionary<string, int>());
                return false;
            }

            int insetX = Math.Max(8, selectedSlot.Value.Width / 6);
            int insetY = Math.Max(8, selectedSlot.Value.Height / 6);
            Rectangle inner = Rectangle.Inflate(selectedSlot.Value, -insetX, -insetY);
            inner = Rectangle.Intersect(inner, new Rectangle(Point.Empty, cap.Size));
            if (inner.Width <= 0 || inner.Height <= 0)
            {
                SaveEquippedSlotDetectionDebug(cap, searchRegion, selectedSlot, inner, 0, 0, false, new Dictionary<string, int>(), new Dictionary<string, int>());
                return false;
            }

            int contentPixels = 0;
            var contentCategories = new Dictionary<string, int>();
            var contentColors = new Dictionary<string, int>();
            for (int y = inner.Top; y < inner.Bottom; y++)
            {
                for (int x = inner.Left; x < inner.Right; x++)
                {
                    Color pixel = cap.GetPixel(x, y);
                    if (TryClassifyEquippedSlotContentPixel(pixel, out string category))
                    {
                        contentPixels++;
                        contentCategories[category] = contentCategories.GetValueOrDefault(category) + 1;

                        string colorKey = ToQuantizedColorKey(pixel);
                        contentColors[colorKey] = contentColors.GetValueOrDefault(colorKey) + 1;
                    }
                }
            }

            int occupiedThreshold = Math.Max(65, inner.Width * inner.Height / 45);
            bool occupied = contentPixels >= occupiedThreshold;
            SaveEquippedSlotDetectionDebug(cap, searchRegion, selectedSlot, inner, contentPixels, occupiedThreshold, occupied, contentCategories, contentColors);



            return occupied;
        }

        private static Rectangle? TryFindSelectedSlotRegion(Bitmap bitmap)
        {
            return ChooseSelectedSlotCandidate(FindSelectedSlotCandidates(bitmap, out _));
        }

        private static List<Rectangle> FindSelectedSlotCandidates(Bitmap bitmap, out int yellowComponentCount)
        {
            var yellowComponents = new List<(Rectangle Rect, int Pixels)>();
            bool[,] visited = new bool[bitmap.Width, bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (visited[x, y] || !IsSelectionYellow(bitmap.GetPixel(x, y)))
                        continue;

                    Rectangle component = FloodFillYellowComponent(bitmap, visited, x, y, out int pixelCount);
                    if (pixelCount >= 4 && component.Width <= 170 && component.Height <= 170)
                        yellowComponents.Add((component, pixelCount));
                }
            }

            yellowComponentCount = yellowComponents.Count;
            return BuildSelectedSlotCandidates(yellowComponents, bitmap, bitmap.Size);
        }

        private static Rectangle? ChooseSelectedSlotCandidate(List<Rectangle> candidates)
        {
            if (candidates.Count == 0)
                return null;

            // 노란 테두리 조각을 이어 만든 후보 중 가장 큰 슬롯형 사각형을 선택해 아이콘 crop의 기준점으로 삼는다.
            return candidates
                .OrderByDescending(rect => rect.Width * rect.Height)
                .First();
        }
        private static bool TryClassifyEquippedSlotContentPixel(Color c, out string category)
        {
            category = "";
            if (IsSelectionYellow(c) || IsSlotFramePixel(c))
                return false;

            int max = Math.Max(c.R, Math.Max(c.G, c.B));
            int min = Math.Min(c.R, Math.Min(c.G, c.B));
            int saturation = max - min;
            bool whiteIcon = max >= 205 && min >= 165 && saturation <= 55;
            bool coloredIcon = max >= 115 && saturation >= 65;

            // 빈 슬롯의 회색 배경과 뒤쪽 캐릭터/패널 밝기는 제외하고, 실제 아이콘의 흰색/고채도 색만 점유 픽셀로 센다.
            if (whiteIcon)
            {
                category = "white";
                return true;
            }

            if (coloredIcon)
            {
                category = "colored";
                return true;
            }

            return false;
        }

        private static string ToQuantizedColorKey(Color c)
        {
            int r = (c.R / 16) * 16;
            int g = (c.G / 16) * 16;
            int b = (c.B / 16) * 16;
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        private void SaveEquippedSlotDetectionDebug(
            Bitmap capture,
            Rectangle screenSearchRegion,
            Rectangle? selectedSlot,
            Rectangle innerRegion,
            int contentPixels,
            int occupiedThreshold,
            bool occupied,
            Dictionary<string, int> contentCategories,
            Dictionary<string, int> contentColors)
        {
            if (!_testModeEnabled)
                return;

            try
            {
                string debugDir = Path.Combine(AppDataPath, "debug", "equipped-slot");
                Directory.CreateDirectory(debugDir);

                string capturePath = Path.Combine(debugDir, "equipped-slot-search.png");
                string annotatedPath = Path.Combine(debugDir, "equipped-slot-search-annotated.png");
                string fullPath = Path.Combine(debugDir, "equipped-slot-full.png");
                string fullAnnotatedPath = Path.Combine(debugDir, "equipped-slot-full-annotated.png");
                string selectedPath = Path.Combine(debugDir, "equipped-slot-selected.png");
                string innerPath = Path.Combine(debugDir, "equipped-slot-inner.png");
                string infoPath = Path.Combine(debugDir, "equipped-slot-info.txt");

                capture.Save(capturePath, ImageFormat.Png);

                using (Bitmap annotated = new(capture))
                using (Graphics g = Graphics.FromImage(annotated))
                using (Pen slotPen = new(Color.Yellow, 3))
                using (Pen innerPen = new(Color.Magenta, 3))
                {
                    if (selectedSlot.HasValue)
                        g.DrawRectangle(slotPen, selectedSlot.Value);
                    if (innerRegion.Width > 0 && innerRegion.Height > 0)
                        g.DrawRectangle(innerPen, innerRegion);
                    annotated.Save(annotatedPath, ImageFormat.Png);
                }

                SaveEquippedSlotFullScreenDebug(fullPath, fullAnnotatedPath, screenSearchRegion, selectedSlot, innerRegion);
                SaveDebugCrop(capture, selectedSlot, selectedPath);
                SaveDebugCrop(capture, innerRegion.Width > 0 && innerRegion.Height > 0 ? innerRegion : null, innerPath);

                Rectangle? absoluteSlot = selectedSlot.HasValue
                    ? new Rectangle(screenSearchRegion.Left + selectedSlot.Value.Left, screenSearchRegion.Top + selectedSlot.Value.Top, selectedSlot.Value.Width, selectedSlot.Value.Height)
                    : null;
                Rectangle? absoluteInner = innerRegion.Width > 0 && innerRegion.Height > 0
                    ? new Rectangle(screenSearchRegion.Left + innerRegion.Left, screenSearchRegion.Top + innerRegion.Top, innerRegion.Width, innerRegion.Height)
                    : null;

                var topColors = contentColors
                    .OrderByDescending(pair => pair.Value)
                    .Take(20)
                    .Select(pair => $"{pair.Key}={pair.Value}");
                var categoryText = contentCategories.Count == 0
                    ? "(none)"
                    : string.Join(", ", contentCategories.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key}={pair.Value}"));

                string info =
                    $"timestamp={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}" +
                    $"occupied={occupied}{Environment.NewLine}" +
                    $"contentPixels={contentPixels}{Environment.NewLine}" +
                    $"occupiedThreshold={occupiedThreshold}{Environment.NewLine}" +
                    $"searchRegion.screen={FormatRectangle(screenSearchRegion)}{Environment.NewLine}" +
                    $"searchCapture.size={capture.Width}x{capture.Height}{Environment.NewLine}" +
                    $"selectedSlot.local={FormatNullableRectangle(selectedSlot)}{Environment.NewLine}" +
                    $"selectedSlot.screen={FormatNullableRectangle(absoluteSlot)}{Environment.NewLine}" +
                    $"innerRegion.local={FormatNullableRectangle(innerRegion.Width > 0 && innerRegion.Height > 0 ? innerRegion : null)}{Environment.NewLine}" +
                    $"innerRegion.screen={FormatNullableRectangle(absoluteInner)}{Environment.NewLine}" +
                    $"detectedCategories={categoryText}{Environment.NewLine}" +
                    $"detectedColors.top20={string.Join(", ", topColors)}{Environment.NewLine}" +
                    $"classifier.white=max>=205,min>=165,saturation<=55{Environment.NewLine}" +
                    $"classifier.colored=max>=115,saturation>=65{Environment.NewLine}" +
                    $"files={fullPath} | {fullAnnotatedPath} | {capturePath} | {annotatedPath} | {selectedPath} | {innerPath}{Environment.NewLine}";

                File.WriteAllText(infoPath, info, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"equipped slot debug save failed: {ex.Message}");
            }
        }

        private static void SaveEquippedSlotFullScreenDebug(string fullPath, string fullAnnotatedPath, Rectangle screenSearchRegion, Rectangle? selectedSlot, Rectangle innerRegion)
        {
            if (!TryGetGameClientRegion(out Rectangle gameRegion))
                return;

            using Bitmap full = new(gameRegion.Width, gameRegion.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(full))
            {
                g.CopyFromScreen(gameRegion.Left, gameRegion.Top, 0, 0, full.Size);
            }

            full.Save(fullPath, ImageFormat.Png);

            Rectangle localSearch = new(screenSearchRegion.Left - gameRegion.Left, screenSearchRegion.Top - gameRegion.Top, screenSearchRegion.Width, screenSearchRegion.Height);
            Rectangle? localSlot = selectedSlot.HasValue
                ? new Rectangle(localSearch.Left + selectedSlot.Value.Left, localSearch.Top + selectedSlot.Value.Top, selectedSlot.Value.Width, selectedSlot.Value.Height)
                : null;
            Rectangle? localInner = innerRegion.Width > 0 && innerRegion.Height > 0
                ? new Rectangle(localSearch.Left + innerRegion.Left, localSearch.Top + innerRegion.Top, innerRegion.Width, innerRegion.Height)
                : null;

            using Bitmap annotated = new(full);
            using Graphics ag = Graphics.FromImage(annotated);
            using Pen searchPen = new(Color.Lime, 4);
            using Pen slotPen = new(Color.Yellow, 4);
            using Pen innerPen = new(Color.Magenta, 4);

            // 전체 화면 디버그는 초록=상단 슬롯 검색 범위, 노랑=선택 슬롯, 자홍=실제 내용물 검사 범위로 표시한다.
            ag.DrawRectangle(searchPen, localSearch);
            if (localSlot.HasValue)
                ag.DrawRectangle(slotPen, localSlot.Value);
            if (localInner.HasValue)
                ag.DrawRectangle(innerPen, localInner.Value);

            annotated.Save(fullAnnotatedPath, ImageFormat.Png);
        }

        private static void SaveDebugCrop(Bitmap source, Rectangle? region, string path)
        {
            if (!region.HasValue || region.Value.Width <= 0 || region.Value.Height <= 0)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }

            Rectangle clipped = Rectangle.Intersect(region.Value, new Rectangle(Point.Empty, source.Size));
            if (clipped.Width <= 0 || clipped.Height <= 0)
                return;

            using Bitmap crop = source.Clone(clipped, PixelFormat.Format32bppArgb);
            crop.Save(path, ImageFormat.Png);
        }
        private static void SaveStratagemIconMatchDebug(
            Bitmap capture,
            Rectangle screenSearchRegion,
            List<Rectangle> slotCandidates,
            Rectangle? selectedSlot,
            Rectangle? iconCrop,
            string? bestName,
            double bestScore,
            string? matchedName,
            string note,
            int yellowComponentCount)
        {
            if (!_testModeEnabled)
                return;

            try
            {
                string debugDir = Path.Combine(AppDataPath, "debug", "stratagem-icon-match");
                Directory.CreateDirectory(debugDir);

                string capturePath = Path.Combine(debugDir, "stratagem-icon-search.png");
                string annotatedPath = Path.Combine(debugDir, "stratagem-icon-search-annotated.png");
                string fullAnnotatedPath = Path.Combine(debugDir, "stratagem-icon-full-annotated.png");
                string selectedPath = Path.Combine(debugDir, "stratagem-icon-selected.png");
                string cropPath = Path.Combine(debugDir, "stratagem-icon-crop.png");
                string infoPath = Path.Combine(debugDir, "stratagem-icon-info.txt");

                capture.Save(capturePath, ImageFormat.Png);

                using (Bitmap annotated = new(capture))
                using (Graphics g = Graphics.FromImage(annotated))
                using (Pen candidatePen = new(Color.Cyan, 2))
                using (Pen selectedPen = new(Color.Yellow, 3))
                using (Pen cropPen = new(Color.Magenta, 3))
                {
                    foreach (Rectangle candidate in slotCandidates)
                        g.DrawRectangle(candidatePen, candidate);
                    if (selectedSlot.HasValue)
                        g.DrawRectangle(selectedPen, selectedSlot.Value);
                    if (iconCrop.HasValue)
                        g.DrawRectangle(cropPen, iconCrop.Value);
                    annotated.Save(annotatedPath, ImageFormat.Png);
                }

                SaveStratagemIconFullScreenDebug(fullAnnotatedPath, screenSearchRegion, slotCandidates, selectedSlot, iconCrop);
                SaveDebugCrop(capture, selectedSlot, selectedPath);
                SaveDebugCrop(capture, iconCrop, cropPath);

                Rectangle? absoluteSlot = selectedSlot.HasValue
                    ? new Rectangle(screenSearchRegion.Left + selectedSlot.Value.Left, screenSearchRegion.Top + selectedSlot.Value.Top, selectedSlot.Value.Width, selectedSlot.Value.Height)
                    : null;
                Rectangle? absoluteCrop = iconCrop.HasValue
                    ? new Rectangle(screenSearchRegion.Left + iconCrop.Value.Left, screenSearchRegion.Top + iconCrop.Value.Top, iconCrop.Value.Width, iconCrop.Value.Height)
                    : null;

                // 테스트모드에서는 노란 조각 연결 결과를 파일로 남겨, 박스가 실제 선택 칸을 덮었는지 바로 확인할 수 있게 한다.
                string info =
                    $"timestamp={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}" +
                    $"note={note}{Environment.NewLine}" +
                    $"yellowComponents={yellowComponentCount}{Environment.NewLine}" +
                    $"candidateCount={slotCandidates.Count}{Environment.NewLine}" +
                    $"searchRegion.screen={FormatRectangle(screenSearchRegion)}{Environment.NewLine}" +
                    $"selectedSlot.local={FormatNullableRectangle(selectedSlot)}{Environment.NewLine}" +
                    $"selectedSlot.screen={FormatNullableRectangle(absoluteSlot)}{Environment.NewLine}" +
                    $"iconCrop.local={FormatNullableRectangle(iconCrop)}{Environment.NewLine}" +
                    $"iconCrop.screen={FormatNullableRectangle(absoluteCrop)}{Environment.NewLine}" +
                    $"best={bestName ?? "(none)"}{Environment.NewLine}" +
                    $"score={bestScore:0.000}{Environment.NewLine}" +
                    $"matched={matchedName ?? "(null)"}{Environment.NewLine}" +
                    $"candidateRects.local={string.Join(" | ", slotCandidates.Select(FormatRectangle))}{Environment.NewLine}" +
                    $"files={capturePath} | {annotatedPath} | {fullAnnotatedPath} | {selectedPath} | {cropPath}{Environment.NewLine}";
                File.WriteAllText(infoPath, info, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"stratagem icon match debug save failed: {ex.Message}");
            }
        }

        private static void SaveStratagemIconFullScreenDebug(string fullAnnotatedPath, Rectangle screenSearchRegion, List<Rectangle> slotCandidates, Rectangle? selectedSlot, Rectangle? iconCrop)
        {
            if (!TryGetGameClientRegion(out Rectangle gameRegion))
                return;

            using Bitmap full = new(gameRegion.Width, gameRegion.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(full))
            {
                g.CopyFromScreen(gameRegion.Left, gameRegion.Top, 0, 0, full.Size);
            }

            Rectangle localSearch = new(screenSearchRegion.Left - gameRegion.Left, screenSearchRegion.Top - gameRegion.Top, screenSearchRegion.Width, screenSearchRegion.Height);
            using Bitmap annotated = new(full);
            using Graphics ag = Graphics.FromImage(annotated);
            using Pen searchPen = new(Color.Lime, 4);
            using Pen candidatePen = new(Color.Cyan, 3);
            using Pen selectedPen = new(Color.Yellow, 4);
            using Pen cropPen = new(Color.Magenta, 4);

            ag.DrawRectangle(searchPen, localSearch);
            foreach (Rectangle candidate in slotCandidates)
                ag.DrawRectangle(candidatePen, new Rectangle(localSearch.Left + candidate.Left, localSearch.Top + candidate.Top, candidate.Width, candidate.Height));
            if (selectedSlot.HasValue)
                ag.DrawRectangle(selectedPen, new Rectangle(localSearch.Left + selectedSlot.Value.Left, localSearch.Top + selectedSlot.Value.Top, selectedSlot.Value.Width, selectedSlot.Value.Height));
            if (iconCrop.HasValue)
                ag.DrawRectangle(cropPen, new Rectangle(localSearch.Left + iconCrop.Value.Left, localSearch.Top + iconCrop.Value.Top, iconCrop.Value.Width, iconCrop.Value.Height));

            annotated.Save(fullAnnotatedPath, ImageFormat.Png);
        }
        private static string FormatRectangle(Rectangle rect)
        {
            return $"x={rect.X}, y={rect.Y}, width={rect.Width}, height={rect.Height}";
        }

        private static string FormatNullableRectangle(Rectangle? rect)
        {
            return rect.HasValue ? FormatRectangle(rect.Value) : "(none)";
        }

        private static void SaveAutoSelectionDebugInfo(string runId, List<string> lines)
        {
            try
            {
                string debugDir = Path.Combine(AppDataPath, "debug", "auto-selection");
                Directory.CreateDirectory(debugDir);

                // 최신 실패를 바로 확인할 수 있게 매 실행 로그를 고정 파일과 runId 파일에 동시에 남긴다.
                string text = string.Join(Environment.NewLine, lines) + Environment.NewLine;
                File.WriteAllText(Path.Combine(debugDir, "auto-selection-info.txt"), text, Encoding.UTF8);
                File.WriteAllText(Path.Combine(debugDir, $"auto-selection-{runId}.txt"), text, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"auto selection debug save failed: {ex.Message}");
            }
        }

        private static string? SaveAutoSelectionScreenCapture(string runId, string label)
        {
            try
            {
                if (!TryGetGameClientRegion(out Rectangle gameRegion))
                    return null;

                string debugDir = Path.Combine(AppDataPath, "debug", "auto-selection");
                Directory.CreateDirectory(debugDir);

                string safeLabel = SanitizeDebugFileName(label);
                string path = Path.Combine(debugDir, $"auto-selection-{runId}-{safeLabel}.png");

                using Bitmap full = new(gameRegion.Width, gameRegion.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(full))
                {
                    g.CopyFromScreen(gameRegion.Left, gameRegion.Top, 0, 0, full.Size);
                }

                full.Save(path, ImageFormat.Png);
                File.Copy(path, Path.Combine(debugDir, $"latest-{safeLabel}.png"), true);
                return path;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"auto selection screenshot save failed: {ex.Message}");
                return null;
            }
        }

        private static string SanitizeDebugFileName(string value)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                builder.Append(invalidChars.Contains(c) ? '_' : c);
            }

            return builder.ToString();
        }

        private static Rectangle FloodFillYellowComponent(Bitmap bitmap, bool[,] visited, int startX, int startY, out int pixelCount)
        {
            int minX = startX, maxX = startX, minY = startY, maxY = startY;
            pixelCount = 0;
            var queue = new Queue<Point>();
            queue.Enqueue(new Point(startX, startY));
            visited[startX, startY] = true;

            while (queue.Count > 0)
            {
                Point p = queue.Dequeue();
                pixelCount++;
                minX = Math.Min(minX, p.X);
                maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y);
                maxY = Math.Max(maxY, p.Y);

                foreach (var next in new[] { new Point(p.X + 1, p.Y), new Point(p.X - 1, p.Y), new Point(p.X, p.Y + 1), new Point(p.X, p.Y - 1) })
                {
                    if (next.X < 0 || next.Y < 0 || next.X >= bitmap.Width || next.Y >= bitmap.Height)
                        continue;

                    if (visited[next.X, next.Y] || !IsSelectionYellow(bitmap.GetPixel(next.X, next.Y)))
                        continue;

                    visited[next.X, next.Y] = true;
                    queue.Enqueue(next);
                }
            }

            return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }

        private async Task<bool> IsStratagemSelectionMenuOpen()
        {
            // 전체 화면 OCR은 팀원 카드의 "준비/장비" 같은 글자에 흔들릴 수 있으므로,
            // 먼저 실제 상세 패널 이름줄에서 스트라타젬명이 읽히는지 확인한다.
            string? visibleStratagemName = await MatchItemFromScreen("스트라타젬");
            if (visibleStratagemName != null)
                return true;

            if (!TryGetGameClientRegion(out Rectangle region))
                return false;

            using var cap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(cap))
            {
                g.CopyFromScreen(region.Left, region.Top, 0, 0, cap.Size);
            }

            string rawText = await RecognizeTextFromBitmap(cap);
            string cleanText = CleanScanText(rawText);

            bool hasSelectionMenuText = cleanText.Contains("보급")
                || cleanText.Contains("방어")
                || cleanText.Contains("공격")
                || cleanText.Contains("스트라타젬");

            if (hasSelectionMenuText)
                return true;

            // 선택창 위에도 하단 "준비"와 상단 "장비 구성"이 OCR에 같이 잡힐 수 있으므로, 선택창 신호가 없을 때만 준비화면으로 본다.
            if (cleanText.Contains("준비") || cleanText.Contains("장비"))
                return false;

            return false;
        }

        private void TriggerStratagem(int slotIndex)
        {
            if (!IsGameActive() || _isChat)
                return;

            string[]? seq = null;

            if (slotIndex == -1)
            {
                seq = new[] { "up", "down", "right", "left", "up" };
            }
            else
            {
                var slots = _currentSlots;

                if (slotIndex < 0 || slotIndex >= slots.Length)
                    return;

                string? name = slots[slotIndex];
                if (string.IsNullOrEmpty(name))
                    return;

                if (!_sequenceMap.TryGetValue(name, out seq) ||
                    seq == null || seq.Length == 0)
                    return;
            }

            // 빈 슬롯이나 조합 누락은 위에서 먼저 걸러 _isSending이 잠긴 채 남지 않게 한다.
            string[] seqArray = seq;
            if (Interlocked.Exchange(ref _isSending, 1) == 1)
                return;

            Task.Run(() =>
            {
                try
                {
                    SendStratagem(seqArray);
                }
                finally
                {
                    Interlocked.Exchange(ref _isSending, 0);
                }
            });
        }

        private void SendStratagem(string[] seqArray)
        {
            if (!TryGetEffectiveStratagemKey("start", out var startVk))
                return;

            var keySequence = new List<uint>();
            foreach (var dir in seqArray)
            {
                if (!TryGetEffectiveStratagemKey(dir.ToLowerInvariant(), out var vk))
                    return;
                keySequence.Add(vk);
            }

            try
            {
                switch (_stratagemType)
                {
                    case "Tap":
                    case "Press":
                        SendInput(startVk, true);
                        Thread.Sleep(_inputDelay);
                        SendInput(startVk, false);
                        break;

                    case "DoubleTap":
                        SendInput(startVk, true);
                        Thread.Sleep(_inputDelay);
                        SendInput(startVk, false);
                        Thread.Sleep(_inputDelay);
                        SendInput(startVk, true);
                        Thread.Sleep(_inputDelay);
                        SendInput(startVk, false);
                        break;

                    case "LongPress":
                        SendInput(startVk, true);
                        Thread.Sleep(300);
                        SendInput(startVk, false);
                        break;

                    case "Hold":
                        SendInput(startVk, true);
                        break;
                }

                Thread.Sleep(_inputDelay);

                foreach (var vk in keySequence)
                {
                    SendInput(vk, true);
                    Thread.Sleep(_inputDelay);
                    SendInput(vk, false);
                    Thread.Sleep(_inputDelay);
                }
            }
            finally
            {
                if (_stratagemType == "Hold")
                    SendInput(startVk, false);
            }
        }

        private void SendInput(uint vk, bool isDown)
        {
            uint flag = vk switch
            {
                0x01 => isDown ? 0x0002u : 0x0004u,
                0x02 => isDown ? 0x0008u : 0x0010u,
                0x04 => isDown ? 0x0020u : 0x0040u,
                0x05 or 0x06 => isDown ? 0x0080u : 0x0100u,
                _ => 0u
            };

            if (flag != 0) mouse_event(flag, 0, 0, vk >= 0x05 ? vk - 4u : 0u, IntPtr.Zero);
            else keybd_event((byte)vk, 0, isDown ? 0u : 2u, UIntPtr.Zero);
        }

        private void ThrowIfAutoSelectionCanceled()
        {
            if (_autoSelectionCts?.IsCancellationRequested == true)
                throw new OperationCanceledException();
        }

        private async Task TapKey(Keys key, int? holdMs = null, int? afterMs = null)
        {
            ThrowIfAutoSelectionCanceled();
            int keyHoldMs = Math.Max(0, holdMs ?? _inputDelay);
            int keyAfterMs = Math.Max(0, afterMs ?? _inputDelay);

            SendInput((uint)key, true);
            try
            {
                await Task.Delay(keyHoldMs);
            }
            finally
            {
                SendInput((uint)key, false);
            }

            await Task.Delay(keyAfterMs);
            ThrowIfAutoSelectionCanceled();
        }

        private uint? ParseInputKey(string? inputKey)
        {
            if (string.IsNullOrWhiteSpace(inputKey))
                return null;

            string enumKey = inputKey;

            switch (inputKey)
            {
                case "mousebuttonleft": enumKey = "LButton"; break;
                case "mousebuttonright": enumKey = "RButton"; break;
                case "mousebuttonmiddle": enumKey = "MButton"; break;
                case "mousebutton4": enumKey = "XButton1"; break;
                case "mousebutton5": enumKey = "XButton2"; break;

                case "backtick": enumKey = "Oemtilde"; break;
                case "minus": enumKey = "OemMinus"; break;
                case "equal": enumKey = "Oemplus"; break;
                case "open bracket": enumKey = "OemOpenBrackets"; break;
                case "close bracket": enumKey = "OemCloseBrackets"; break;
                case "backslash": enumKey = "OemPipe"; break;
                case "semicolon": enumKey = "OemSemicolon"; break;
                case "quote": enumKey = "OemQuotes"; break;
                case "comma": enumKey = "OemComma"; break;
                case "period": enumKey = "OemPeriod"; break;
                case "slash": enumKey = "OemQuestion"; break;
                case "backspace": enumKey = "Back"; break;

                case "left ctrl": enumKey = "LControlKey"; break;
                case "right ctrl": enumKey = "RControlKey"; break;
                case "left alt": enumKey = "LMenu"; break;
                case "right alt": enumKey = "RMenu"; break;
                case "left shift": enumKey = "LShiftKey"; break;
                case "right shift": enumKey = "RShiftKey"; break;
                case "kana": enumKey = "HangulMode"; break;
                case "kanji": enumKey = "HanjaMode"; break;
                case "caps lock": enumKey = "CapsLock"; break;
                case "page up": enumKey = "PageUp"; break;
                case "page down": enumKey = "PageDown"; break;

                case "numpad 0":
                case "numpad 1":
                case "numpad 2":
                case "numpad 3":
                case "numpad 4":
                case "numpad 5":
                case "numpad 6":
                case "numpad 7":
                case "numpad 8":
                case "numpad 9":
                    enumKey = "NumPad" + inputKey[^1];
                    break;

                case "numpad *": enumKey = "Multiply"; break;
                case "numpad +": enumKey = "Add"; break;
                case "numpad -": enumKey = "Subtract"; break;
                case "numpad .": enumKey = "Decimal"; break;
                case "numpad /": enumKey = "Divide"; break;
            }

            if (Enum.TryParse<Keys>(enumKey, true, out var parsedKey))
                return (uint)parsedKey;

            return null;
        }

        private string GetKeyName(uint value)
        {
            if (value == 0) return "없음";
            if (value >= 0x1001) return ((PadButton)value).ToString();

            var specialKeys = new Dictionary<Keys, string>
            {
                { Keys.LButton, "마우스 왼쪽" },
                { Keys.RButton, "마우스 오른쪽" },
                { Keys.MButton, "마우스 휠 클릭" },
                { Keys.XButton1, "마우스 버튼1" },
                { Keys.XButton2, "마우스 버튼2" },
                { Keys.Oemtilde, "` ~" },
                { Keys.OemMinus, "- _" },
                { Keys.Oemplus, "= +" },
                { Keys.OemOpenBrackets, "[ {" },
                { Keys.OemCloseBrackets, "] }" },
                { Keys.OemPipe, "\\ |" },
                { Keys.OemSemicolon, "; :" },
                { Keys.OemQuotes, "' \"" },
                { Keys.Oemcomma, ", <" },
                { Keys.OemPeriod, ". >" },
                { Keys.OemQuestion, "/ ?" },
                { Keys.Space, "Space" },
                { Keys.Return, "Enter" },
                { Keys.Back, "Backspace" },
                { Keys.ControlKey, "Ctrl" },
                { Keys.LControlKey, "LCtrl" },
                { Keys.RControlKey, "RCtrl" },
                { Keys.Menu, "Alt" },
                { Keys.LMenu, "LAlt" },
                { Keys.RMenu, "RAlt" },
                { Keys.ShiftKey, "Shift" },
                { Keys.LShiftKey, "LShift" },
                { Keys.RShiftKey, "RShift" },
                { Keys.LWin, "LWin" },
                { Keys.RWin, "RWin" },
                { Keys.HangulMode, "한/영" },
                { Keys.HanjaMode, "한자" },
                { Keys.CapsLock, "CapsLock" },
                { Keys.Escape, "ESC" },
                { Keys.Tab, "Tab" },
                { Keys.Delete, "Del" },
                { Keys.Insert, "Ins" },
                { Keys.Home, "Home" },
                { Keys.End, "End" },
                { Keys.PageUp, "PgUp" },
                { Keys.PageDown, "PgDn" },
                { Keys.Scroll, "Scroll" },
                { Keys.Pause, "Pause" },
                { Keys.Left, "←" },
                { Keys.Up, "↑" },
                { Keys.Right, "→" },
                { Keys.Down, "↓" },
                { Keys.NumPad0, "Num0" },
                { Keys.NumPad1, "Num1" },
                { Keys.NumPad2, "Num2" },
                { Keys.NumPad3, "Num3" },
                { Keys.NumPad4, "Num4" },
                { Keys.NumPad5, "Num5" },
                { Keys.NumPad6, "Num6" },
                { Keys.NumPad7, "Num7" },
                { Keys.NumPad8, "Num8" },
                { Keys.NumPad9, "Num9" },
                { Keys.NumLock, "NumLock" },
                { Keys.Multiply, "Num*" },
                { Keys.Add, "Num+" },
                { Keys.Subtract, "Num-" },
                { Keys.Decimal, "Num." },
                { Keys.Divide, "Num/" }
            };

            Keys k = (Keys)value;
            if (specialKeys.ContainsKey(k))
            {
                return specialKeys[k];
            }

            string name = k.ToString();
            if (name.Length == 2 && name.StartsWith("D") && char.IsDigit(name[1]))
            {
                return name.Substring(1);
            }

            return name;
        }

        private bool OverlayShow()
        {
            if (!IsGameActive() || _isChat || CursorUtil.IsVisible())
                return false;

            var slotNames = _currentSlots
                .Select((name, index) => new { Name = name, Visible = index < _overlaySlotVisibility.Length && _overlaySlotVisibility[index] })
                .Where(slot => slot.Visible && !string.IsNullOrEmpty(slot.Name))
                .Select(slot => slot.Name!)
                .ToArray();
            if (slotNames.Length == 0)
                return false;

            var images = slotNames
                .Select(name => GetStratagemImage(name!))
                .ToArray();

            if (_overlayForm == null) _overlayForm = new OverlayForm(slotNames!, images!);
            else _overlayForm.UpdateSlot(slotNames!, images!);

            _overlayForm.Show();
            ApplyCaptureExclusion(_overlayForm);
            return true;
        }

        private void SetOverlayStratagemViewKey(bool isDown)
        {
            if (isDown)
            {
                if (_heldOverlayStratagemViewKey != 0 || !IsGameActive() || _isChat)
                    return;

                if (!TryGetEffectiveStratagemKey("start", out uint startVk) || startVk == _overlayKey)
                    return;

                // 오버레이를 보는 동안 수동 보기 키가 있으면 우선하고, 없으면 게임 설정 키를 hold한다.
                SendInput(startVk, true);
                _heldOverlayStratagemViewKey = startVk;
                return;
            }

            if (_heldOverlayStratagemViewKey == 0)
                return;

            ReleaseOverlayStratagemViewKey("오버레이 단축키 해제");
        }

        private void ReleaseOverlayStratagemViewKey(string reason)
        {
            if (_heldOverlayStratagemViewKey == 0)
                return;

            // 상태를 먼저 비워 재진입 중 중복 해제를 막고, 실제로 눌렀던 키만 정확히 놓는다.
            uint heldKey = _heldOverlayStratagemViewKey;
            _heldOverlayStratagemViewKey = 0;
            SendInput(heldKey, false);
            Logger.Log($"오버레이 스트라타젬 보기 키 강제 해제: {reason}, VK={heldKey}");
        }

        private void OverlayHide()
        {
            // 닫기 경로가 단축키 KeyUp이 아니어도 Ctrl 같은 보기 키가 눌린 채 남지 않게 한다.
            ReleaseOverlayStratagemViewKey("오버레이 숨김");

            if (_overlayForm != null)
            {
                string? selected = _overlayForm.Selected;

                _overlayForm.Hide();

                if (!string.IsNullOrEmpty(selected))
                {
                    Task.Run(async () =>
                    {
                        await Task.Delay(50);

                        int Index = Array.IndexOf(_currentSlots ?? Array.Empty<string>(), selected);
                        if (Index != -1)
                            TriggerStratagem(Index);
                    });
                }
            }
        }

        public record PresetSummary(string Id, string Name, PresetLoadout Loadout, SupportWeaponAssistSettings SupportWeapon, bool[] OverlaySlots, string EquipmentPresetId, Image?[] PreviewImages);
        public record EquipmentPresetSummary(string Id, string Name, PresetLoadout Loadout, Image?[] PreviewImages);

        public class PresetLoadout
        {
            public string[] Stratagems { get; init; } = Array.Empty<string>();
            public string Armor { get; init; } = "";
            public string Primary { get; init; } = "";
            public string Secondary { get; init; } = "";
            public string Grenade { get; init; } = "";

            public static PresetLoadout FromJson(JsonElement loadoutElement)
            {
                // 저장된 프리셋도 현재 설정한 기본+여분 슬롯 수에 맞춰 읽는다.
                var stratagems = new string[StratagemSlotCount];
                if (loadoutElement.ValueKind == JsonValueKind.Object
                    && loadoutElement.TryGetProperty("stratagems", out var stratagemsElement)
                    && stratagemsElement.ValueKind == JsonValueKind.Array)
                {
                    int index = 0;
                    foreach (var item in stratagemsElement.EnumerateArray())
                    {
                        if (index >= stratagems.Length) break;
                        stratagems[index++] = item.GetString() ?? "";
                    }
                }

                return new PresetLoadout
                {
                    Stratagems = stratagems,
                    Armor = GetLoadoutProperty(loadoutElement, "armor"),
                    Primary = GetLoadoutProperty(loadoutElement, "primary"),
                    Secondary = GetLoadoutProperty(loadoutElement, "secondary"),
                    Grenade = GetLoadoutProperty(loadoutElement, "grenade")
                };
            }

            private static string GetLoadoutProperty(JsonElement element, string name)
            {
                return element.ValueKind == JsonValueKind.Object
                    && element.TryGetProperty(name, out var property)
                    && property.ValueKind == JsonValueKind.String
                    ? property.GetString() ?? ""
                    : "";
            }
        }

        public class SoftwareCursorOverlayForm : Form
        {
            protected override bool ShowWithoutActivation => true;

            private Point cursorScreenPoint;
            private Rectangle lastCursorInvalidateBounds = Rectangle.Empty;

            public SoftwareCursorOverlayForm()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                StartPosition = FormStartPosition.Manual;
                BackColor = Color.Magenta;
                TransparencyKey = Color.Magenta;
                Bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1, 1);
                DoubleBuffered = true;
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_EX_LAYERED = 0x00080000;
                    const int WS_EX_TRANSPARENT = 0x00000020;
                    const int WS_EX_TOOLWINDOW = 0x00000080;
                    const int WS_EX_NOACTIVATE = 0x08000000;
                    CreateParams cp = base.CreateParams;
                    // 자체 커서는 보기 전용이어야 하므로 클릭과 포커스는 모두 실제 F3/F4 창으로 통과시킨다.
                    cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                    return cp;
                }
            }

            public void UpdateCursor(Point screenPoint)
            {
                Rectangle bounds = Screen.FromPoint(screenPoint).Bounds;
                if (Bounds != bounds)
                {
                    Bounds = bounds;
                    lastCursorInvalidateBounds = Rectangle.Empty;
                    Invalidate();
                }

                if (cursorScreenPoint == screenPoint && !lastCursorInvalidateBounds.IsEmpty)
                    return;

                Rectangle oldBounds = lastCursorInvalidateBounds;
                cursorScreenPoint = screenPoint;
                Rectangle nextBounds = GetCursorInvalidateBounds(PointToClient(screenPoint));

                // 커서가 지나간 자리와 새 위치만 다시 그려 전체 화면 투명 레이어 갱신 비용을 줄인다.
                if (!oldBounds.IsEmpty)
                    Invalidate(oldBounds);

                Invalidate(nextBounds);
                lastCursorInvalidateBounds = nextBounds;
            }

            private static Rectangle GetCursorInvalidateBounds(Point p)
            {
                Rectangle bounds = new(p.X - 5, p.Y - 5, 32, 40);
                bounds.Inflate(4, 4);
                return bounds;
            }

            public void EnsureTopMost()
            {
                if (!IsHandleCreated) return;

                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                Point p = PointToClient(cursorScreenPoint);
                Point[] arrow =
                {
                    new(p.X, p.Y),
                    new(p.X, p.Y + 22),
                    new(p.X + 6, p.Y + 17),
                    new(p.X + 10, p.Y + 28),
                    new(p.X + 15, p.Y + 26),
                    new(p.X + 11, p.Y + 15),
                    new(p.X + 19, p.Y + 15)
                };

                using var outline = new Pen(Color.Black, 3) { LineJoin = LineJoin.Round };
                using var fill = new SolidBrush(Color.White);
                e.Graphics.FillPolygon(fill, arrow);
                e.Graphics.DrawPolygon(outline, arrow);
            }
        }
        public class HelperEditorWindow : Form
        {
            protected override bool ShowWithoutActivation => true;

            private readonly MainForm owner;
            private readonly WebView2 webView;
            private bool isWebViewInitialized;
            private bool isAdjustingClientSize;
            private int layoutClientWidth = BaseClientWidth;
            private bool closeNotified = true;

            public HelperEditorWindow(MainForm owner)
            {
                this.owner = owner;
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                Text = "헬다이버즈2 보조 기구 - 프리셋 편집";
                FormBorderStyle = FormBorderStyle.None;
                MaximizeBox = false;
                TopMost = true;
                ShowInTaskbar = false;
                BackColor = Color.FromArgb(0x22, 0x22, 0x22);
                ClientSize = GetInitialClientSize();
                MinimumSize = SizeFromClientSize(new Size((int)Math.Round(BaseClientWidth * MinClientScale), (int)Math.Round(CurrentBaseClientHeight * MinClientScale)));
                StartPosition = FormStartPosition.CenterScreen;
                Resize += (_, _) => KeepClientAspectRatio();

                // 원본 MainForm을 숨기거나 다시 띄우지 않기 위해, 같은 index.html을 별도 WebView에 올린 편집 전용 창이다.
                webView = new WebView2
                {
                    Dock = DockStyle.Fill,
                    DefaultBackgroundColor = Color.FromArgb(0x22, 0x22, 0x22)
                };
                Controls.Add(webView);
            }

            protected override async void OnShown(EventArgs e)
            {
                base.OnShown(e);
                closeNotified = false;
                EnsureTopMost();

                if (isWebViewInitialized)
                    return;

                isWebViewInitialized = true;
                await owner.InitializeEditorWebView(webView);
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_EX_TOOLWINDOW = 0x00000080;
                    const int WS_EX_NOACTIVATE = 0x08000000;
                    CreateParams cp = base.CreateParams;
                    // 마우스 입력은 편집창이 받되, 창 활성화는 막아 헬다이버즈2 포커스를 유지한다.
                    cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                    return cp;
                }
            }

            public void EnsureTopMost()
            {
                if (!IsHandleCreated) return;

                // 게임 창 위에 편집창이 묻히지 않도록 표시할 때마다 TopMost 순서를 다시 고정한다.
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }

            public bool CanReceiveForwardedKeyboardInput => Visible && !IsDisposed && webView.CoreWebView2 != null;

            public void InsertTextFromHook(int backspaceCount, string text)
            {
                RunEditorInputScript(BuildEditorInputScript("insert", text, backspaceCount));
            }

            public void SendEditingKeyFromHook(string key)
            {
                RunEditorInputScript(BuildEditorInputScript(key, "", 0));
            }

            public void SelectAllFromHook()
            {
                RunEditorInputScript(BuildEditorInputScript("selectAll", "", 0));
            }

            public void PasteClipboardFromHook()
            {
                if (IsDisposed) return;

                BeginInvoke(new Action(() =>
                {
                    string text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                    RunEditorInputScript(BuildEditorInputScript("insert", text, 0));
                }));
            }

            public void CopySelectionFromHook(bool cut)
            {
                RunEditorInputScript(BuildEditorInputScript(cut ? "cut" : "copy", "", 0));
            }

            public void SetModifierStateFromHook(bool ctrl, bool shift)
            {
                string ctrlJson = ctrl ? "true" : "false";
                string shiftJson = shift ? "true" : "false";
                RunEditorInputScript($"window.hd2ModifierState = {{ ctrl: {ctrlJson}, shift: {shiftJson} }};");
            }

            public void SaveCurrentPresetFromHook()
            {
                // Ctrl+S 저장은 입력칸 상태와 무관하므로 페이지에 노출한 전역 저장 함수만 직접 호출한다.
                RunEditorInputScript("(() => window.hd2SaveCurrentPresetFromHook?.() ?? false)();");
            }

            private void RunEditorInputScript(string script)
            {
                if (IsDisposed || webView.CoreWebView2 == null)
                    return;

                void Execute()
                {
                    if (!IsDisposed && webView.CoreWebView2 != null)
                        _ = webView.CoreWebView2.ExecuteScriptAsync(script);
                }

                if (InvokeRequired) BeginInvoke(new Action(Execute));
                else Execute();
            }

            private static string BuildEditorInputScript(string command, string text, int backspaceCount)
            {
                string commandJson = JsonSerializer.Serialize(command);
                string textJson = JsonSerializer.Serialize(text);
                int safeBackspaceCount = Math.Max(0, backspaceCount);

                return $$"""
(() => {
  const command = {{commandJson}};
  const text = {{textJson}};
  const backspaceCount = {{safeBackspaceCount}};
  const editableTypes = new Set(['text', 'search', 'password', 'email', 'url', 'tel', 'number']);
  const isEditable = (el) => {
    if (!el) return false;
    const tag = (el.tagName || '').toLowerCase();
    if (el.isContentEditable) return true;
    if (tag === 'textarea') return true;
    if (tag !== 'input') return false;
    return editableTypes.has((el.type || 'text').toLowerCase());
  };
  const isVisible = (el) => !!(el && (el.offsetWidth || el.offsetHeight || el.getClientRects().length));
  if (command === 'savePreset') {
    // F3 편집창은 게임 포커스를 유지하므로 Ctrl+S를 후킹해 현재 선택된 프리셋 저장으로 전달한다.
    if (typeof saveSelectedPreset === 'function' && typeof selectedPresetId === 'string' && selectedPresetId) {
      saveSelectedPreset();
      return true;
    }
    return false;
  }
  let el = document.activeElement;
  if (!isEditable(el)) {
    el = [...document.querySelectorAll('#modal-search, .modal-search-input, .preset-rename-inline, input[type="text"], input[type="search"], textarea, [contenteditable="true"]')]
      .find(candidate => isEditable(candidate) && isVisible(candidate) && !candidate.disabled && !candidate.readOnly);
    if (el) el.focus({ preventScroll: true });
  }
  if (!isEditable(el)) return false;

  const emitInput = (inputType, data = null) => {
    try { el.dispatchEvent(new InputEvent('input', { bubbles: true, inputType, data })); }
    catch { el.dispatchEvent(new Event('input', { bubbles: true })); }
    el.dispatchEvent(new Event('change', { bubbles: true }));
  };

  const getSelectionText = () => {
    if (el.isContentEditable) return String(window.getSelection()?.toString() || '');
    const start = el.selectionStart ?? 0;
    const end = el.selectionEnd ?? start;
    return String(el.value || '').slice(start, end);
  };

  const insertText = (value, eraseBefore = 0) => {
    if (el.isContentEditable) {
      for (let i = 0; i < eraseBefore; i++) document.execCommand('delete', false);
      if (value) document.execCommand('insertText', false, value);
      emitInput('insertText', value);
      return true;
    }

    const current = String(el.value || '');
    let start = el.selectionStart ?? current.length;
    let end = el.selectionEnd ?? start;
    if (start === end && eraseBefore > 0) start = Math.max(0, start - eraseBefore);
    el.value = current.slice(0, start) + value + current.slice(end);
    const nextPos = start + value.length;
    el.setSelectionRange(nextPos, nextPos);
    emitInput(value ? 'insertText' : 'deleteContentBackward', value || null);
    return true;
  };

  const deleteForward = () => {
    if (el.isContentEditable) {
      document.execCommand('forwardDelete', false);
      emitInput('deleteContentForward');
      return true;
    }
    const current = String(el.value || '');
    let start = el.selectionStart ?? current.length;
    let end = el.selectionEnd ?? start;
    if (start === end) end = Math.min(current.length, end + 1);
    el.value = current.slice(0, start) + current.slice(end);
    el.setSelectionRange(start, start);
    emitInput('deleteContentForward');
    return true;
  };

  const moveCaret = (where) => {
    if (el.isContentEditable) return false;
    const current = String(el.value || '');
    let start = el.selectionStart ?? current.length;
    let end = el.selectionEnd ?? start;
    let pos = start;
    if (where === 'ArrowLeft') pos = Math.max(0, start - 1);
    else if (where === 'ArrowRight') pos = Math.min(current.length, end + 1);
    else if (where === 'Home') pos = 0;
    else if (where === 'End') pos = current.length;
    el.setSelectionRange(pos, pos);
    return true;
  };

  if (command === 'insert') return insertText(text, backspaceCount);
  if (command === 'Backspace') return insertText('', 1);
  if (command === 'Delete') return deleteForward();
  if (command === 'Enter') return insertText('\n', 0);
  if (command === 'ArrowLeft' || command === 'ArrowRight' || command === 'Home' || command === 'End') return moveCaret(command);
  if (command === 'selectAll') {
    if (el.isContentEditable) document.execCommand('selectAll', false);
    else el.select();
    return true;
  }
  if (command === 'copy' || command === 'cut') {
    const selected = getSelectionText();
    if (selected && navigator.clipboard?.writeText) navigator.clipboard.writeText(selected).catch(() => {});
    if (command === 'cut' && selected) insertText('', 0);
    return true;
  }
  return false;
})();
""";
            }
            protected override void OnVisibleChanged(EventArgs e)
            {
                base.OnVisibleChanged(e);

                if (Visible)
                {
                    closeNotified = false;
                    return;
                }

                NotifyClosedOnce();
            }

            private void NotifyClosedOnce()
            {
                if (closeNotified)
                    return;

                closeNotified = true;
                // 편집창을 닫아도 헬다이버즈2에는 별도 키 입력을 보내지 않는다.
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    owner.NotifyHelperEditorWindowClosedByUser();
                    Hide();
                    return;
                }

                base.OnFormClosing(e);
            }

            private void KeepClientAspectRatio()
            {
                if (isAdjustingClientSize || WindowState != FormWindowState.Normal) return;

                int width = ClientSize.Width;
                int height = ClientSize.Height;
                if (width <= 0 || height <= 0) return;

                int minWidth = (int)Math.Round(layoutClientWidth * MinClientScale);
                int minHeight = (int)Math.Round(CurrentBaseClientHeight * MinClientScale);
                double targetRatio = (double)layoutClientWidth / CurrentBaseClientHeight;
                width = Math.Max(width, minWidth);
                height = Math.Max(height, minHeight);
                int targetHeight = (int)Math.Round(width / targetRatio);
                int targetWidth = (int)Math.Round(height * targetRatio);

                Size nextSize = Math.Abs(targetHeight - height) <= Math.Abs(targetWidth - width)
                    ? new Size(width, targetHeight)
                    : new Size(targetWidth, height);

                if (nextSize == ClientSize) return;

                isAdjustingClientSize = true;
                ClientSize = nextSize;
                isAdjustingClientSize = false;
            }

            public void ApplySettingsPanelClientWidth(int targetBaseWidth)
            {
                // F3 편집창도 설정 패널 펼침 상태에 맞춰 실제 창 폭을 조절한다.
                if (WindowState != FormWindowState.Normal)
                {
                    layoutClientWidth = targetBaseWidth;
                    return;
                }

                double scale = ClientSize.Height > 0
                    ? Math.Max(MinClientScale, (double)ClientSize.Height / CurrentBaseClientHeight)
                    : 1.0;
                Size nextSize = new(
                    (int)Math.Round(targetBaseWidth * scale),
                    (int)Math.Round(CurrentBaseClientHeight * scale)
                );

                layoutClientWidth = targetBaseWidth;
                MinimumSize = SizeFromClientSize(new Size(
                    (int)Math.Round(targetBaseWidth * MinClientScale),
                    (int)Math.Round(CurrentBaseClientHeight * MinClientScale)
                ));

                if (ClientSize == nextSize) return;

                isAdjustingClientSize = true;
                ClientSize = nextSize;
                isAdjustingClientSize = false;
            }

            public void ApplyStratagemSlotClientHeight()
            {
                if (WindowState != FormWindowState.Normal)
                    return;

                // F3 편집창도 메인 창과 같은 가로 배율로 추가 슬롯 행 높이를 반영한다.
                double scale = ClientSize.Width > 0
                    ? Math.Max(MinClientScale, (double)ClientSize.Width / layoutClientWidth)
                    : 1.0;
                Size nextSize = new(
                    (int)Math.Round(layoutClientWidth * scale),
                    (int)Math.Round(CurrentBaseClientHeight * scale)
                );

                MinimumSize = SizeFromClientSize(new Size(
                    (int)Math.Round(layoutClientWidth * MinClientScale),
                    (int)Math.Round(CurrentBaseClientHeight * MinClientScale)
                ));

                if (ClientSize == nextSize) return;

                isAdjustingClientSize = true;
                ClientSize = nextSize;
                isAdjustingClientSize = false;
            }
        }
        public class PresetOverlayForm : Form
        {
            protected override bool ShowWithoutActivation => true;

            private readonly Action<PresetSummary> onStratagemPresetSelected;
            private readonly Action<EquipmentPresetSummary> onEquipmentPresetSelected;
            private readonly Action? onClosed;
            private readonly FlowLayoutPanel stratagemList = new();
            private readonly FlowLayoutPanel equipmentList = new();
            private List<PresetSummary> stratagemPresets = new();
            private List<EquipmentPresetSummary> equipmentPresets = new();
            private string selectedStratagemPresetId = "";
            private string selectedEquipmentPresetId = "";

            public PresetOverlayForm(
                Action<PresetSummary> onStratagemSelected,
                Action<EquipmentPresetSummary> onEquipmentSelected,
                Action? onClosed)
            {
                onStratagemPresetSelected = onStratagemSelected;
                onEquipmentPresetSelected = onEquipmentSelected;
                this.onClosed = onClosed;
                Text = "프리셋 전환";
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.CenterScreen;
                Size = new Size(800, 560);
                MinimumSize = new Size(600, 380);
                BackColor = Color.FromArgb(18, 18, 18);
                ForeColor = Color.White;
                TopMost = true;
                KeyPreview = true;

                BuildLayout();
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_EX_TOOLWINDOW = 0x00000080;
                    const int WS_EX_NOACTIVATE = 0x08000000;
                    CreateParams cp = base.CreateParams;
                    // F4 프리셋 창은 게임을 보조하는 오버레이이므로 표시/클릭 시 입력 포커스를 가져오지 않는다.
                    cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                    return cp;
                }
            }

            public void UpdatePresets(
                List<PresetSummary> nextStratagemPresets,
                List<EquipmentPresetSummary> nextEquipmentPresets,
                string selectedStratagemId,
                string selectedEquipmentId)
            {
                stratagemPresets = nextStratagemPresets;
                equipmentPresets = nextEquipmentPresets;
                selectedStratagemPresetId = selectedStratagemId;
                selectedEquipmentPresetId = selectedEquipmentId;
                RenderPresetCards();
            }

            private void BuildLayout()
            {
                var root = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(14),
                    RowCount = 5,
                    BackColor = BackColor
                };
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
                Controls.Add(root);

                var title = new Label
                {
                    Text = "프리셋 전환",
                    Font = new Font("맑은 고딕", 15, FontStyle.Bold),
                    ForeColor = Color.FromArgb(255, 225, 20),
                    AutoSize = true,
                    Margin = new Padding(0, 0, 0, 10)
                };
                root.Controls.Add(title, 0, 0);

                root.Controls.Add(CreateSectionTitle("스트라타젬 프리셋"), 0, 1);
                ConfigureList(stratagemList);
                root.Controls.Add(stratagemList, 0, 2);
                root.Controls.Add(CreateSectionTitle("장비 프리셋"), 0, 3);
                ConfigureList(equipmentList);
                root.Controls.Add(equipmentList, 0, 4);
            }

            private static Label CreateSectionTitle(string text) => new()
            {
                Text = text,
                Font = new Font("맑은 고딕", 10, FontStyle.Bold),
                ForeColor = Color.Gainsboro,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 5)
            };

            private void ConfigureList(FlowLayoutPanel list)
            {
                list.Dock = DockStyle.Fill;
                list.AutoScroll = true;
                list.WrapContents = true;
                list.BackColor = BackColor;
                list.Padding = new Padding(0, 0, 0, 6);
            }

            private void RenderPresetCards()
            {
                stratagemList.SuspendLayout();
                equipmentList.SuspendLayout();
                stratagemList.Controls.Clear();
                equipmentList.Controls.Clear();
                foreach (var preset in stratagemPresets)
                    stratagemList.Controls.Add(CreatePresetCard(preset.Name, preset.PreviewImages, preset.Id == selectedStratagemPresetId, () => onStratagemPresetSelected(preset)));
                foreach (var preset in equipmentPresets)
                    equipmentList.Controls.Add(CreatePresetCard(preset.Name, preset.PreviewImages, preset.Id == selectedEquipmentPresetId, () => onEquipmentPresetSelected(preset)));
                stratagemList.ResumeLayout();
                equipmentList.ResumeLayout();
            }

            private Control CreatePresetCard(string presetName, Image?[] previewImages, bool selected, Action onSelected)
            {
                var card = new Panel
                {
                    Width = 220,
                    Height = 116,
                    Margin = new Padding(0, 0, 10, 10),
                    BackColor = selected ? Color.FromArgb(58, 55, 18) : Color.FromArgb(30, 30, 30),
                    Cursor = Cursors.Hand
                };
                card.Paint += (_, e) =>
                {
                    using var pen = new Pen(selected ? Color.FromArgb(255, 225, 20) : Color.FromArgb(70, 70, 70), 2);
                    e.Graphics.DrawRectangle(pen, 1, 1, card.Width - 3, card.Height - 3);
                };

                var name = new Label
                {
                    Text = presetName,
                    ForeColor = Color.White,
                    Font = new Font("맑은 고딕", 10, FontStyle.Bold),
                    AutoEllipsis = true,
                    Location = new Point(10, 8),
                    Size = new Size(198, 24)
                };
                card.Controls.Add(name);

                for (int i = 0; i < previewImages.Length; i++)
                {
                    var picture = new PictureBox
                    {
                        Size = new Size(42, 42),
                        Location = new Point(10 + (i % 4) * 50, 36 + (i / 4) * 30),
                        SizeMode = PictureBoxSizeMode.Zoom,
                        BackColor = Color.FromArgb(14, 14, 14),
                        Image = previewImages[i]
                    };
                    card.Controls.Add(picture);
                }

                void SelectPreset(object? _, EventArgs __) => onSelected();
                card.Click += SelectPreset;
                foreach (Control child in card.Controls)
                    child.Click += SelectPreset;

                return card;
            }

            public void EnsureTopMost()
            {
                if (!IsHandleCreated) return;

                // 프리셋 전환창은 포커스를 뺏지 않는 창이라, 단축키로 다시 열 때 게임 뒤로 밀리지 않게 순서를 복구한다.
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                EnsureTopMost();
            }
            protected override void OnVisibleChanged(EventArgs e)
            {
                base.OnVisibleChanged(e);
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    // 닫기 버튼은 사용자의 명시적 닫기라서 포커스 복원 시 다시 띄우지 않는다.
                    onClosed?.Invoke();
                    Hide();
                    return;
                }

                base.OnFormClosing(e);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (e.KeyCode == Keys.Escape)
                {
                    // ESC로 닫을 때도 사용자가 닫은 것으로 처리한다.
                    onClosed?.Invoke();
                    Hide();
                    e.Handled = true;
                }
            }
        }

        public class OcrRegionSettings
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public int BorderThickness { get; set; }

            public static OcrRegionSettings DefaultFor(string targetType)
            {
                return targetType == "스트라타젬"
                    // 실제 게임 화면에서 맞춘 스트라타젬 이름줄 OCR 기본 영역이다.
                    ? new OcrRegionSettings { X = 540, Y = 406, Width = 929, Height = 45, BorderThickness = 2 }
                    : new OcrRegionSettings { X = 890, Y = 580, Width = 530, Height = 35, BorderThickness = 2 };
            }

            public OcrRegionSettings Clone()
            {
                return new OcrRegionSettings
                {
                    X = X,
                    Y = Y,
                    Width = Width,
                    Height = Height,
                    BorderThickness = BorderThickness
                };
            }

            public OcrRegionSettings Normalized()
            {
                return new OcrRegionSettings
                {
                    X = Math.Clamp(X, 0, 1919),
                    Y = Math.Clamp(Y, 0, 1079),
                    Width = Math.Clamp(Width, 10, 1920),
                    Height = Math.Clamp(Height, 10, 1080),
                    BorderThickness = Math.Clamp(BorderThickness, 1, 12)
                };
            }

            public OcrRegionSettings WithProperty(string property, int value)
            {
                var next = Clone();
                switch (property.ToLowerInvariant())
                {
                    case "x": next.X = value; break;
                    case "y": next.Y = value; break;
                    case "width": next.Width = value; break;
                    case "height": next.Height = value; break;
                    case "border": next.BorderThickness = value; break;
                }

                return next;
            }
        }

        public class AutoReloadSettings
        {
            // 1920x1080 기준 중앙의 "탭 R : 무기 장전" 안내가 HUD 커브 값에 따라 위아래로 움직이는 범위다.
            public int X { get; set; } = 820;
            public int Y { get; set; } = 0;
            public int Width { get; set; } = 360;
            public int Height { get; set; } = 230;
            public int BorderThickness { get; set; } = 2;
            public int MinimumPromptMatches { get; set; } = 2;

            public AutoReloadSettings Clone() => new()
            {
                X = X,
                Y = Y,
                Width = Width,
                Height = Height,
                BorderThickness = BorderThickness,
                MinimumPromptMatches = MinimumPromptMatches
            };

            public AutoReloadSettings Normalized() => new()
            {
                X = Math.Clamp(X, 0, 1919),
                Y = Math.Clamp(Y, 0, 1079),
                Width = Math.Clamp(Width, 30, 800),
                Height = Math.Clamp(Height, 30, 400),
                BorderThickness = Math.Clamp(BorderThickness, 1, 12),
                MinimumPromptMatches = Math.Clamp(MinimumPromptMatches, 1, 2)
            };

            public AutoReloadSettings WithProperty(string property, int value)
            {
                AutoReloadSettings next = Clone();
                switch (property.ToLowerInvariant())
                {
                    case "x": next.X = value; break;
                    case "y": next.Y = value; break;
                    case "width": next.Width = value; break;
                    case "height": next.Height = value; break;
                    case "border": next.BorderThickness = value; break;
                    case "minimumpromptmatches": next.MinimumPromptMatches = value; break;
                }

                return next;
            }
        }

        private readonly record struct AutoReloadDetectionResult(
            bool IsEmpty,
            int KeywordMatches,
            int RequiredKeywordMatches,
            string RawText,
            Rectangle Region,
            string Note)
        {
            public static AutoReloadDetectionResult Empty => new(false, 0, 0, "", Rectangle.Empty, "대기 중");
        }

        private class AutoReloadCalibrationForm : Form
        {
            private readonly Action<AutoReloadSettings> onSettingsChanged;
            private readonly Action<AutoReloadSettings> onPreviewChanged;
            private readonly Func<AutoReloadSettings, Task<AutoReloadDetectionResult>> onTestRequested;
            private readonly NumericUpDown xInput = new();
            private readonly NumericUpDown yInput = new();
            private readonly NumericUpDown widthInput = new();
            private readonly NumericUpDown heightInput = new();
            private readonly NumericUpDown borderInput = new();
            private readonly NumericUpDown minimumPromptMatchesInput = new();
            private readonly Label testResult = new();
            private AutoReloadSettings settings;
            private bool loading;

            public AutoReloadCalibrationForm(
                AutoReloadSettings initialSettings,
                Action<AutoReloadSettings> onSettingsChanged,
                Action<AutoReloadSettings> onPreviewChanged,
                Func<AutoReloadSettings, Task<AutoReloadDetectionResult>> onTestRequested)
            {
                this.onSettingsChanged = onSettingsChanged;
                this.onPreviewChanged = onPreviewChanged;
                this.onTestRequested = onTestRequested;
                settings = initialSettings.Normalized();

                Text = "자동 재장전 안내 보정";
                ClientSize = new Size(470, 370);
                MinimumSize = new Size(470, 370);
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterScreen;
                MaximizeBox = false;
                MinimizeBox = false;
                BackColor = Color.FromArgb(35, 35, 35);
                ForeColor = Color.White;

                BuildLayout();
                ApplySettings(settings);
            }

            public void ApplySettings(AutoReloadSettings next)
            {
                loading = true;
                settings = next.Normalized();
                xInput.Value = settings.X;
                yInput.Value = settings.Y;
                widthInput.Value = settings.Width;
                heightInput.Value = settings.Height;
                borderInput.Value = settings.BorderThickness;
                minimumPromptMatchesInput.Value = settings.MinimumPromptMatches;
                loading = false;
                onPreviewChanged(settings);
            }

            private void BuildLayout()
            {
                var root = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(16),
                    ColumnCount = 2,
                    RowCount = 9,
                    BackColor = BackColor
                };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
                Controls.Add(root);

                AddInputRow(root, 0, "좌우 위치", xInput, 0, 1919);
                AddInputRow(root, 1, "상하 위치", yInput, 0, 1079);
                AddInputRow(root, 2, "너비", widthInput, 30, 800);
                AddInputRow(root, 3, "높이", heightInput, 30, 400);
                AddInputRow(root, 4, "박스 굵기", borderInput, 1, 12);
                AddInputRow(root, 5, "최소 일치 문구", minimumPromptMatchesInput, 1, 2);

                var testButton = CreateButton("테스트");
                testButton.Click += async (_, _) => await RunTestAsync();
                root.Controls.Add(testButton, 0, 6);

                testResult.AutoSize = false;
                testResult.Dock = DockStyle.Fill;
                testResult.TextAlign = ContentAlignment.MiddleLeft;
                testResult.ForeColor = Color.Gainsboro;
                root.Controls.Add(testResult, 1, 6);

                var resetButton = CreateButton("기본값");
                resetButton.Click += (_, _) =>
                {
                    ApplySettings(new AutoReloadSettings());
                    NotifyChanged();
                };
                root.Controls.Add(resetButton, 0, 7);

                var copyButton = CreateButton("복사");
                copyButton.Click += (_, _) => CopySettings();
                root.Controls.Add(copyButton, 1, 7);

                var closeButton = CreateButton("닫기");
                closeButton.Click += (_, _) => Close();
                root.SetColumnSpan(closeButton, 2);
                root.Controls.Add(closeButton, 0, 8);
            }

            private void AddInputRow(TableLayoutPanel root, int row, string labelText, NumericUpDown input, int minimum, int maximum)
            {
                var label = new Label
                {
                    Text = labelText,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = Color.Gainsboro
                };
                root.Controls.Add(label, 0, row);

                input.Dock = DockStyle.Fill;
                input.Minimum = minimum;
                input.Maximum = maximum;
                input.BackColor = Color.FromArgb(25, 25, 25);
                input.ForeColor = Color.White;
                input.ValueChanged += (_, _) => NotifyChanged();
                root.Controls.Add(input, 1, row);
            }

            private Button CreateButton(string text) => new()
            {
                Text = text,
                Dock = DockStyle.Fill,
                Height = 32,
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(4)
            };

            private void NotifyChanged()
            {
                if (loading) return;

                settings = new AutoReloadSettings
                {
                    X = (int)xInput.Value,
                    Y = (int)yInput.Value,
                    Width = (int)widthInput.Value,
                    Height = (int)heightInput.Value,
                    BorderThickness = (int)borderInput.Value,
                    MinimumPromptMatches = (int)minimumPromptMatchesInput.Value
                }.Normalized();

                onSettingsChanged(settings.Clone());
                onPreviewChanged(settings);
            }

            private async Task RunTestAsync()
            {
                AutoReloadDetectionResult result = await onTestRequested(settings);
                string state = result.IsEmpty ? "장전 안내 감지" : "미감지";
                string recognized = string.IsNullOrWhiteSpace(result.RawText) ? "(없음)" : result.RawText;
                testResult.Text = $"{state}  문구 {result.KeywordMatches}/{result.RequiredKeywordMatches}: {recognized}";
                testResult.ForeColor = result.IsEmpty ? Color.FromArgb(255, 100, 100) : Color.LightGreen;
            }

            private void CopySettings()
            {
                AutoReloadSettings value = settings.Normalized();
                Clipboard.SetText(
                    "자동 재장전 인식 설정" + Environment.NewLine +
                    $"x={value.X}, y={value.Y}, width={value.Width}, height={value.Height}, border={value.BorderThickness}, minimumPromptMatches={value.MinimumPromptMatches}" + Environment.NewLine + Environment.NewLine +
                    "settings.ini" + Environment.NewLine +
                    "autoReload.promptRegionVersion=1" + Environment.NewLine +
                    $"autoReload.x={value.X}" + Environment.NewLine +
                    $"autoReload.y={value.Y}" + Environment.NewLine +
                    $"autoReload.width={value.Width}" + Environment.NewLine +
                    $"autoReload.height={value.Height}" + Environment.NewLine +
                    $"autoReload.border={value.BorderThickness}" + Environment.NewLine +
                    $"autoReload.minimumPromptMatches={value.MinimumPromptMatches}");
            }
        }

        public class OcrRegionSettingsForm : Form
        {
            private readonly Action<Dictionary<string, OcrRegionSettings>> onSettingsChanged;
            private readonly Action<string, OcrRegionSettings> onPreviewChanged;
            private readonly Func<string, OcrRegionSettings, Task<string>> onTestRequested;
            private readonly Dictionary<string, OcrRegionSettings> workingSettings = new();
            private readonly ComboBox typeBox = new();
            private readonly NumericUpDown xInput = new();
            private readonly NumericUpDown yInput = new();
            private readonly NumericUpDown widthInput = new();
            private readonly NumericUpDown heightInput = new();
            private readonly NumericUpDown borderInput = new();
            private readonly Label testResultLabel = new();
            private bool isLoading;

            public OcrRegionSettingsForm(Dictionary<string, OcrRegionSettings> initialSettings, Action<Dictionary<string, OcrRegionSettings>> onSettingsChanged, Action<string, OcrRegionSettings> onPreviewChanged, Func<string, OcrRegionSettings, Task<string>> onTestRequested)
            {
                this.onSettingsChanged = onSettingsChanged;
                this.onPreviewChanged = onPreviewChanged;
                this.onTestRequested = onTestRequested;
                Text = "OCR 영역 설정";
                StartPosition = FormStartPosition.CenterScreen;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                BackColor = Color.FromArgb(34, 34, 34);
                ForeColor = Color.White;
                ClientSize = new Size(400, 350);

                var root = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(14),
                    ColumnCount = 2,
                    RowCount = 8
                };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                Controls.Add(root);

                typeBox.DropDownStyle = ComboBoxStyle.DropDownList;
                typeBox.Items.AddRange(new object[] { "스트라타젬", "주 무기", "보조 무기", "방어구", "투척 무기" });
                typeBox.SelectedIndexChanged += (_, _) => LoadSelectedType();
                AddRow(root, 0, "항목", typeBox);

                ConfigureNumber(xInput, 0, 1919);
                ConfigureNumber(yInput, 0, 1079);
                ConfigureNumber(widthInput, 10, 1920);
                ConfigureNumber(heightInput, 10, 1080);
                ConfigureNumber(borderInput, 1, 12);

                AddRow(root, 1, "좌우 위치", xInput);
                AddRow(root, 2, "상하 위치", yInput);
                AddRow(root, 3, "너비", widthInput);
                AddRow(root, 4, "높이", heightInput);
                AddRow(root, 5, "박스 굵기", borderInput);
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

                var testPanel = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 2,
                    RowCount = 1
                };
                testPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
                testPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                var testButton = CreateButton("테스트");
                testButton.Click += async (_, _) => await RunOcrTest();
                testResultLabel.Text = "읽은 글자: -";
                testResultLabel.Dock = DockStyle.Fill;
                testResultLabel.TextAlign = ContentAlignment.MiddleLeft;
                testResultLabel.ForeColor = Color.Gainsboro;
                testResultLabel.AutoEllipsis = true;
                testPanel.Controls.Add(testButton, 0, 0);
                testPanel.Controls.Add(testResultLabel, 1, 0);
                root.Controls.Add(testPanel, 0, 6);
                root.SetColumnSpan(testPanel, 2);

                var buttonPanel = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    WrapContents = false
                };
                var closeButton = CreateButton("닫기");
                closeButton.Click += (_, _) => Close();
                var copyButton = CreateButton("복사");
                copyButton.Click += (_, _) => CopySettingsToClipboard();
                var resetButton = CreateButton("기본값");
                resetButton.Click += (_, _) => ResetSelectedType();
                buttonPanel.Controls.Add(closeButton);
                buttonPanel.Controls.Add(copyButton);
                buttonPanel.Controls.Add(resetButton);
                root.Controls.Add(buttonPanel, 0, 7);
                root.SetColumnSpan(buttonPanel, 2);

                ApplySettings(initialSettings);
            }

            public void ApplySettings(Dictionary<string, OcrRegionSettings> settings)
            {
                isLoading = true;
                workingSettings.Clear();
                foreach (string type in typeBox.Items.Cast<string>())
                {
                    workingSettings[type] = settings.TryGetValue(type, out var region)
                        ? region.Normalized()
                        : OcrRegionSettings.DefaultFor(type);
                }

                if (typeBox.SelectedIndex < 0)
                    typeBox.SelectedIndex = 0;
                isLoading = false;
                LoadSelectedType();
            }

            private static void AddRow(TableLayoutPanel root, int row, string labelText, Control control)
            {
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, row == 0 ? 40 : 36));

                var label = new Label
                {
                    Text = labelText,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = Color.Gainsboro
                };

                control.Dock = DockStyle.Fill;
                root.Controls.Add(label, 0, row);
                root.Controls.Add(control, 1, row);
            }

            private void ConfigureNumber(NumericUpDown input, int min, int max)
            {
                input.Minimum = min;
                input.Maximum = max;
                input.DecimalPlaces = 0;
                input.Increment = 1;
                input.BackColor = Color.FromArgb(24, 24, 24);
                input.ForeColor = Color.White;
                input.ValueChanged += (_, _) => SaveCurrentType();
            }

            private static Button CreateButton(string text)
            {
                return new Button
                {
                    Text = text,
                    Width = 90,
                    Height = 34,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(24, 24, 24),
                    ForeColor = Color.White
                };
            }

            private void LoadSelectedType()
            {
                if (typeBox.SelectedItem is not string type || !workingSettings.TryGetValue(type, out var settings))
                    return;

                isLoading = true;
                var normalized = settings.Normalized();
                xInput.Value = normalized.X;
                yInput.Value = normalized.Y;
                widthInput.Value = normalized.Width;
                heightInput.Value = normalized.Height;
                borderInput.Value = normalized.BorderThickness;
                isLoading = false;
                onPreviewChanged(type, normalized);
            }

            private void SaveCurrentType()
            {
                if (isLoading || typeBox.SelectedItem is not string type)
                    return;

                // 숫자 입력이 바뀌면 즉시 저장해 다음 OCR 테스트에서 같은 범위를 쓰게 한다.
                workingSettings[type] = new OcrRegionSettings
                {
                    X = (int)xInput.Value,
                    Y = (int)yInput.Value,
                    Width = (int)widthInput.Value,
                    Height = (int)heightInput.Value,
                    BorderThickness = (int)borderInput.Value
                }.Normalized();

                onSettingsChanged(workingSettings.ToDictionary(item => item.Key, item => item.Value.Clone()));
                onPreviewChanged(type, workingSettings[type]);
                testResultLabel.Text = "읽은 글자: -";
            }

            private void ResetSelectedType()
            {
                if (typeBox.SelectedItem is not string type)
                    return;

                workingSettings[type] = OcrRegionSettings.DefaultFor(type);
                LoadSelectedType();
                SaveCurrentType();
            }

            private void CopySettingsToClipboard()
            {
                SaveCurrentType();

                // 테스트로 맞춘 OCR 좌표를 채팅에 바로 붙여넣을 수 있게 전체 항목을 텍스트로 정리한다.
                Clipboard.SetText(BuildSettingsText());
                MessageBox.Show(this, "OCR 영역 설정을 클립보드에 복사했습니다.", "복사 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            private async Task RunOcrTest()
            {
                if (typeBox.SelectedItem is not string type)
                    return;

                SaveCurrentType();
                testResultLabel.Text = "읽는 중...";

                // 현재 선택 항목의 영역을 즉시 캡처해 설정창 안에서만 테스트 판독 결과를 보여준다.
                string text = await onTestRequested(type, workingSettings[type]);
                testResultLabel.Text = $"읽은 글자: {text}";
            }

            private string BuildSettingsText()
            {
                var builder = new StringBuilder();
                builder.AppendLine("OCR 영역 설정");

                foreach (string type in typeBox.Items.Cast<string>())
                {
                    var settings = workingSettings.TryGetValue(type, out var region)
                        ? region.Normalized()
                        : OcrRegionSettings.DefaultFor(type);

                    builder.AppendLine($"{type}: x={settings.X}, y={settings.Y}, width={settings.Width}, height={settings.Height}, border={settings.BorderThickness}");
                }

                builder.AppendLine();
                builder.AppendLine("settings.ini");
                foreach (string type in typeBox.Items.Cast<string>())
                {
                    var settings = workingSettings.TryGetValue(type, out var region)
                        ? region.Normalized()
                        : OcrRegionSettings.DefaultFor(type);

                    builder.AppendLine($"ocr.{type}.x={settings.X}");
                    builder.AppendLine($"ocr.{type}.y={settings.Y}");
                    builder.AppendLine($"ocr.{type}.width={settings.Width}");
                    builder.AppendLine($"ocr.{type}.height={settings.Height}");
                    builder.AppendLine($"ocr.{type}.border={settings.BorderThickness}");
                }

                return builder.ToString().TrimEnd();
            }
        }

        public class OcrDebugOverlayForm : Form
        {
            protected override bool ShowWithoutActivation => true;

            private readonly Label titleLabel = new();
            private readonly Label rawLabel = new();
            private readonly Label matchLabel = new();
            private readonly System.Windows.Forms.Timer hideTimer = new() { Interval = 3500 };

            public OcrDebugOverlayForm()
            {
                // 자동선택 중 OCR 판독값과 DB 매칭명을 게임 화면 위에 잠깐 띄우는 반투명 확인창이다.
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                BackColor = Color.FromArgb(18, 18, 18);
                Opacity = 0.78;
                Size = new Size(520, 112);
                StartPosition = FormStartPosition.Manual;

                var root = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(12, 8, 12, 8),
                    RowCount = 3,
                    BackColor = BackColor
                };
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
                Controls.Add(root);

                titleLabel.ForeColor = Color.FromArgb(255, 225, 20);
                titleLabel.Font = new Font("맑은 고딕", 9, FontStyle.Bold);
                titleLabel.Dock = DockStyle.Fill;
                root.Controls.Add(titleLabel, 0, 0);

                rawLabel.ForeColor = Color.White;
                rawLabel.Font = new Font("맑은 고딕", 10, FontStyle.Regular);
                rawLabel.Dock = DockStyle.Fill;
                rawLabel.AutoEllipsis = true;
                root.Controls.Add(rawLabel, 0, 1);

                matchLabel.ForeColor = Color.Gainsboro;
                matchLabel.Font = new Font("맑은 고딕", 10, FontStyle.Bold);
                matchLabel.Dock = DockStyle.Fill;
                matchLabel.AutoEllipsis = true;
                root.Controls.Add(matchLabel, 0, 2);

                hideTimer.Tick += (_, _) =>
                {
                    hideTimer.Stop();
                    Hide();
                };
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_EX_TOOLWINDOW = 0x00000080;
                    const int WS_EX_NOACTIVATE = 0x08000000;
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                    return cp;
                }
            }

            public void ShowResult(string targetType, string rawText, string? matchedName, double similarity)
            {
                var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
                Location = new Point(screen.Left + 24, screen.Top + 90);

                titleLabel.Text = $"OCR 확인 - {targetType}";
                rawLabel.Text = $"읽음: {TrimForOverlay(rawText)}";
                matchLabel.Text = matchedName == null
                    ? $"매칭 실패 ({similarity:P0})"
                    : $"매칭: {matchedName} ({similarity:P0})";
                matchLabel.ForeColor = matchedName == null ? Color.FromArgb(255, 120, 120) : Color.FromArgb(140, 255, 170);

                hideTimer.Stop();
                Show();
                hideTimer.Start();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    hideTimer.Dispose();

                base.Dispose(disposing);
            }

            private static string TrimForOverlay(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return "(없음)";

                return value.Length <= 80 ? value : value[..80] + "...";
            }
        }

        public class StratagemSelectionDebugForm : Form
        {
            protected override bool ShowWithoutActivation => true;

            private readonly PictureBox startImageBox = new();
            private readonly PictureBox arrivalImageBox = new();
            private readonly PictureBox targetImageBox = new();
            private readonly Label startLabel = new();
            private readonly Label arrivalLabel = new();
            private readonly Label targetLabel = new();
            private readonly System.Windows.Forms.Timer hideTimer = new() { Interval = 2000 };

            public StratagemSelectionDebugForm()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                BackColor = Color.FromArgb(18, 18, 18);
                Opacity = 0.84;
                Size = new Size(540, 170);
                StartPosition = FormStartPosition.Manual;

                var root = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(10),
                    ColumnCount = 3,
                    RowCount = 2,
                    BackColor = BackColor
                };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                Controls.Add(root);

                AddDebugColumn(root, 0, startImageBox, startLabel);
                AddDebugColumn(root, 1, arrivalImageBox, arrivalLabel);
                AddDebugColumn(root, 2, targetImageBox, targetLabel);

                hideTimer.Tick += (_, _) =>
                {
                    hideTimer.Stop();
                    Hide();
                };
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_EX_TOOLWINDOW = 0x00000080;
                    const int WS_EX_NOACTIVATE = 0x08000000;
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                    return cp;
                }
            }

            public void ShowResult(
                string? startName,
                double? startImageScore,
                string? startPosition,
                Image? startImage,
                string? arrivalName,
                double? arrivalImageScore,
                string? arrivalPosition,
                Image? arrivalImage,
                string targetName,
                string? targetPosition,
                Image? targetImage)
            {
                var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
                Location = new Point(screen.Left + 24, screen.Top + 210);

                // 표시창이 원본 이미지 객체 수명에 묶이지 않도록 각 이미지는 내부 복사본으로 교체한다.
                SetImage(startImageBox, startImage);
                SetImage(arrivalImageBox, arrivalImage);
                SetImage(targetImageBox, targetImage);

                startLabel.Text = $"시작: {DisplayName(startName)}\n위치: {DisplayPosition(startPosition)}\n이미지: {DisplayScore(startImageScore)}";
                arrivalLabel.Text = $"도착: {DisplayName(arrivalName)}\n위치: {DisplayPosition(arrivalPosition)}\n이미지: {DisplayScore(arrivalImageScore)}";
                targetLabel.Text = $"목표: {DisplayName(targetName)}\n위치: {DisplayPosition(targetPosition)}";
                arrivalLabel.ForeColor = string.Equals(arrivalName, targetName, StringComparison.Ordinal)
                    ? Color.FromArgb(140, 255, 170)
                    : Color.FromArgb(255, 170, 90);

                hideTimer.Stop();
                Show();
                hideTimer.Start();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    hideTimer.Dispose();
                    DisposeImage(startImageBox);
                    DisposeImage(arrivalImageBox);
                    DisposeImage(targetImageBox);
                }

                base.Dispose(disposing);
            }

            private static void AddDebugColumn(TableLayoutPanel root, int column, PictureBox imageBox, Label label)
            {
                imageBox.Dock = DockStyle.Fill;
                imageBox.SizeMode = PictureBoxSizeMode.Zoom;
                imageBox.BackColor = Color.FromArgb(10, 10, 10);
                root.Controls.Add(imageBox, column, 0);

                label.Dock = DockStyle.Fill;
                label.TextAlign = ContentAlignment.MiddleCenter;
                label.ForeColor = Color.White;
                label.Font = new Font("맑은 고딕", 8, FontStyle.Bold);
                label.AutoEllipsis = false;
                root.Controls.Add(label, column, 1);
            }

            private static void SetImage(PictureBox box, Image? image)
            {
                DisposeImage(box);
                box.Image = image == null ? null : new Bitmap(image);
            }

            private static void DisposeImage(PictureBox box)
            {
                Image? old = box.Image;
                box.Image = null;
                old?.Dispose();
            }

            private static string DisplayName(string? value)
            {
                return string.IsNullOrWhiteSpace(value) ? "없음/실패" : value;
            }

            private static string DisplayScore(double? score)
            {
                return score.HasValue ? $"{score.Value:P1}" : "-";
            }

            private static string DisplayPosition(string? position)
            {
                return string.IsNullOrWhiteSpace(position) ? "-" : position;
            }
        }

        public class OcrRegionOverlayForm : Form
        {
            protected override bool ShowWithoutActivation => true;

            private readonly System.Windows.Forms.Timer hideTimer = new() { Interval = 3500 };
            private int borderThickness = 2;
            private Color borderColor = Color.Red;

            public OcrRegionOverlayForm()
            {
                // OCR 판독 범위만 눈으로 확인하기 위한 클릭 통과형 붉은 테두리 디버그 창이다.
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
                BackColor = Color.Magenta;
                TransparencyKey = Color.Magenta;
                StartPosition = FormStartPosition.Manual;

                hideTimer.Tick += (_, _) =>
                {
                    hideTimer.Stop();
                    Hide();
                };
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_EX_TOOLWINDOW = 0x00000080;
                    const int WS_EX_TRANSPARENT = 0x00000020;
                    const int WS_EX_LAYERED = 0x00080000;
                    const int WS_EX_NOACTIVATE = 0x08000000;
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE;
                    return cp;
                }
            }

            public void ShowRegion(Rectangle region, int thickness, int? durationMs = null, Color? color = null)
            {
                borderThickness = Math.Clamp(thickness, 1, 12);
                borderColor = color ?? Color.Red;
                Bounds = region;
                hideTimer.Stop();
                Show();
                Invalidate();

                if (durationMs.HasValue)
                {
                    hideTimer.Interval = Math.Max(200, durationMs.Value);
                    hideTimer.Start();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                using var pen = new Pen(borderColor, borderThickness);
                float inset = borderThickness / 2f;
                e.Graphics.DrawRectangle(pen, inset, inset, Math.Max(0, Width - borderThickness - 1), Math.Max(0, Height - borderThickness - 1));
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    hideTimer.Dispose();

                base.Dispose(disposing);
            }
        }

        public class CrosshairSettings
        {
            public bool Enabled { get; set; } = false;
            public bool ShowOnlyWhileRightMouseButtonHeld { get; set; } = false;
            // 0이면 기존처럼 비조준 상태에서 완전히 숨기고, 1~100이면 힙파이어 조준점을 흐리게 남긴다.
            public int NonAimingOpacity { get; set; } = 0;
            public int OverallSize { get; set; } = 100;
            public string DotStyle { get; set; } = "filled";
            public int DotSize { get; set; } = 6;
            public int DotThickness { get; set; } = 2;
            public string DotColor { get; set; } = "#00FF66";
            public int DotOpacity { get; set; } = 100;
            public int CrosshairGap { get; set; } = 10;
            public int CrosshairLength { get; set; } = 18;
            public int CrosshairThickness { get; set; } = 3;
            public string CrosshairColor { get; set; } = "#00FF66";
            public int CrosshairOpacity { get; set; } = 100;
            public int OutlineThickness { get; set; } = 0;
            public string OutlineColor { get; set; } = "#000000";
            public int OutlineOpacity { get; set; } = 100;

            public CrosshairSettings Normalized()
            {
                return new CrosshairSettings
                {
                    Enabled = Enabled,
                    ShowOnlyWhileRightMouseButtonHeld = ShowOnlyWhileRightMouseButtonHeld,
                    NonAimingOpacity = Math.Clamp(NonAimingOpacity, 0, 100),
                    OverallSize = Math.Clamp(OverallSize, 50, 200),
                    DotStyle = DotStyle is "filled" or "hollow" or "none" ? DotStyle : "filled",
                    DotSize = Math.Clamp(DotSize, 1, 80),
                    DotThickness = Math.Clamp(DotThickness, 1, 30),
                    DotColor = NormalizeHexColor(DotColor, "#00FF66"),
                    DotOpacity = Math.Clamp(DotOpacity, 0, 100),
                    CrosshairGap = Math.Clamp(CrosshairGap, 0, 100),
                    CrosshairLength = Math.Clamp(CrosshairLength, 1, 120),
                    CrosshairThickness = Math.Clamp(CrosshairThickness, 1, 40),
                    CrosshairColor = NormalizeHexColor(CrosshairColor, "#00FF66"),
                    CrosshairOpacity = Math.Clamp(CrosshairOpacity, 0, 100),
                    OutlineThickness = Math.Clamp(OutlineThickness, 0, 40),
                    OutlineColor = NormalizeHexColor(OutlineColor, "#000000"),
                    OutlineOpacity = Math.Clamp(OutlineOpacity, 0, 100)
                };
            }

            private static string NormalizeHexColor(string? value, string fallback)
            {
                if (string.IsNullOrWhiteSpace(value)) return fallback;

                string hex = value.Trim();
                if (!hex.StartsWith("#")) hex = "#" + hex;
                return Regex.IsMatch(hex, "^#[0-9a-fA-F]{6}$") ? hex.ToUpperInvariant() : fallback;
            }
        }


        public class SupportWeaponAssistSettings
        {
            public string Mode { get; set; } = "Off";
            public bool GaugeVisible { get; set; } = true;
            public bool GaugeAlwaysRefresh { get; set; } = true;
            public int GaugeOpacity { get; set; } = 85;
            public int GaugeOffsetX { get; set; } = 0;
            public int GaugeOffsetY { get; set; } = -72;
            public bool GaugeVerticalMode { get; set; } = false;
            public int VerticalGaugeOffsetX { get; set; } = 120;
            public int VerticalGaugeOffsetY { get; set; } = 0;
            public int GaugeWidth { get; set; } = 220;
            public int GaugeHeight { get; set; } = 14;
            public int WarningVolume { get; set; } = 60;
            public string WarningSoundPath { get; set; } = "";
            public double AutoFireReleaseSeconds { get; set; } = 2.6;

            public SupportWeaponAssistSettings Normalized()
            {
                return new SupportWeaponAssistSettings
                {
                    Mode = NormalizeMode(Mode),
                    GaugeVisible = GaugeVisible,
                    GaugeAlwaysRefresh = GaugeAlwaysRefresh,
                    GaugeOpacity = Math.Clamp(GaugeOpacity, 0, 100),
                    GaugeOffsetX = Math.Clamp(GaugeOffsetX, -500, 500),
                    GaugeOffsetY = Math.Clamp(GaugeOffsetY, -500, 500),
                    GaugeVerticalMode = GaugeVerticalMode,
                    VerticalGaugeOffsetX = Math.Clamp(VerticalGaugeOffsetX, -500, 500),
                    VerticalGaugeOffsetY = Math.Clamp(VerticalGaugeOffsetY, -500, 500),
                    GaugeWidth = Math.Clamp(GaugeWidth, 40, 800),
                    GaugeHeight = Math.Clamp(GaugeHeight, 4, 80),
                    WarningVolume = Math.Clamp(WarningVolume, 0, 100),
                    WarningSoundPath = WarningSoundPath?.Trim() ?? "",
                    AutoFireReleaseSeconds = Math.Clamp(AutoFireReleaseSeconds, 0.1, 5.0)
                };
            }

            public static string NormalizeMode(string? mode)
            {
                return mode switch
                {
                    "AutoFire" => "AutoFire",
                    "AutoRepeat" => "AutoRepeat",
                    "Danger" => "Danger",
                    _ => "Off"
                };
            }
        }

        public class CrosshairEditorForm : Form
        {
            private readonly Action<CrosshairSettings> onSettingsChanged;
            private readonly CrosshairPreviewPanel preview = new();
            private CrosshairSettings settings;
            private bool isApplying;

            public CrosshairEditorForm(CrosshairSettings initialSettings, Action<CrosshairSettings> onChanged)
            {
                settings = initialSettings.Normalized();
                onSettingsChanged = onChanged;

                Text = "조준점 제작";
                StartPosition = FormStartPosition.CenterScreen;
                Size = new Size(920, 680);
                MinimumSize = new Size(760, 560);
                BackColor = Color.FromArgb(0x22, 0x22, 0x22);
                ForeColor = Color.White;
                ShowInTaskbar = false;
                TopMost = true;

                BuildLayout();
                ApplySettings(settings);
            }

            public void ApplySettings(CrosshairSettings nextSettings)
            {
                isApplying = true;
                settings = nextSettings.Normalized();
                preview.Settings = settings;
                foreach (Control control in GetAllControls(this))
                    SyncControlFromSettings(control);
                isApplying = false;
            }

            private void BuildLayout()
            {
                var root = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 2,
                    RowCount = 1,
                    Padding = new Padding(16),
                    BackColor = BackColor
                };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                preview.Dock = DockStyle.Fill;
                preview.Margin = new Padding(0, 0, 16, 0);
                root.Controls.Add(preview, 0, 0);

                var editor = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    AutoScroll = true,
                    ColumnCount = 2,
                    RowCount = 0,
                    BackColor = BackColor
                };
                editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                root.Controls.Add(editor, 1, 0);

                AddCheck(editor, "사용", "Enabled");
                AddCheck(editor, "우클릭 조준시 표시", "ShowOnlyWhileRightMouseButtonHeld");
                AddSlider(editor, "비조준시 투명도", "NonAimingOpacity", 0, 100);
                AddCombo(editor, "중앙점 형태", "DotStyle", new[] { ("filled", "속찬 점"), ("hollow", "빈 점"), ("none", "없음") });
                AddSlider(editor, "전체 크기", "OverallSize", 50, 200);
                AddSlider(editor, "중앙점 크기", "DotSize", 1, 80);
                AddSlider(editor, "중앙점 두께", "DotThickness", 1, 30);
                AddColor(editor, "중앙점 색상", "DotColor");
                AddSlider(editor, "중앙점 투명도", "DotOpacity", 0, 100);
                AddSlider(editor, "십자선 간격", "CrosshairGap", 0, 100);
                AddSlider(editor, "십자선 길이", "CrosshairLength", 1, 120);
                AddSlider(editor, "십자선 두께", "CrosshairThickness", 1, 40);
                AddColor(editor, "십자선 색상", "CrosshairColor");
                AddSlider(editor, "십자선 투명도", "CrosshairOpacity", 0, 100);
                AddSlider(editor, "테두리 두께", "OutlineThickness", 0, 40);
                AddColor(editor, "테두리 색상", "OutlineColor");
                AddSlider(editor, "테두리 투명도", "OutlineOpacity", 0, 100);

                Controls.Add(root);
            }

            private void AddRow(TableLayoutPanel editor, Control row)
            {
                int index = editor.Controls.Count;
                editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                editor.Controls.Add(row, index % 2, index / 2);
            }

            private Panel MakeRow(string label, Control input)
            {
                var panel = new Panel { Height = 64, Dock = DockStyle.Top, Padding = new Padding(0, 0, 12, 8) };
                var title = new Label { Text = label, Dock = DockStyle.Top, Height = 22, ForeColor = Color.Gainsboro };
                input.Dock = DockStyle.Fill;
                panel.Controls.Add(input);
                panel.Controls.Add(title);
                return panel;
            }

            private void AddCheck(TableLayoutPanel editor, string label, string key)
            {
                var check = new CheckBox { Tag = key, ForeColor = Color.White, Text = "켜기", AutoSize = true };
                check.CheckedChanged += (_, _) => { if (!isApplying) UpdateSetting(key, check.Checked); };
                AddRow(editor, MakeRow(label, check));
            }

            private void AddCombo(TableLayoutPanel editor, string label, string key, (string Value, string Text)[] items)
            {
                var combo = new ComboBox { Tag = key, DropDownStyle = ComboBoxStyle.DropDownList };
                combo.DisplayMember = "Text";
                combo.ValueMember = "Value";
                combo.DataSource = items.Select(item => new { item.Value, item.Text }).ToList();
                combo.SelectedValueChanged += (_, _) => { if (!isApplying) UpdateSetting(key, combo.SelectedValue?.ToString() ?? "filled"); };
                AddRow(editor, MakeRow(label, combo));
            }

            private void AddSlider(TableLayoutPanel editor, string label, string key, int min, int max)
            {
                var panel = new TableLayoutPanel { Tag = key, ColumnCount = 2, Dock = DockStyle.Fill };
                panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));

                var track = new TrackBar { Minimum = min, Maximum = max, TickStyle = TickStyle.None, Dock = DockStyle.Fill, Tag = key };
                var number = new NumericUpDown { Minimum = min, Maximum = max, Dock = DockStyle.Fill, Tag = key };
                track.ValueChanged += (_, _) =>
                {
                    if (isApplying) return;
                    number.Value = track.Value;
                    UpdateSetting(key, track.Value);
                };
                number.ValueChanged += (_, _) =>
                {
                    if (isApplying) return;
                    track.Value = (int)number.Value;
                    UpdateSetting(key, (int)number.Value);
                };

                panel.Controls.Add(track, 0, 0);
                panel.Controls.Add(number, 1, 0);
                AddRow(editor, MakeRow(label, panel));
            }

            private void AddColor(TableLayoutPanel editor, string label, string key)
            {
                var button = new Button { Tag = key, Text = "색상", Dock = DockStyle.Fill };
                button.Click += (_, _) =>
                {
                    using var dialog = new ColorDialog { Color = GetColorSetting(key), FullOpen = true };
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                        UpdateSetting(key, ColorTranslator.ToHtml(dialog.Color));
                };
                AddRow(editor, MakeRow(label, button));
            }

            private void UpdateSetting(string key, object value)
            {
                switch (key)
                {
                    case "Enabled": settings.Enabled = Convert.ToBoolean(value); break;
                    case "ShowOnlyWhileRightMouseButtonHeld": settings.ShowOnlyWhileRightMouseButtonHeld = Convert.ToBoolean(value); break;
                    case "NonAimingOpacity": settings.NonAimingOpacity = Convert.ToInt32(value); break;
                    case "OverallSize": settings.OverallSize = Convert.ToInt32(value); break;
                    case "DotStyle": settings.DotStyle = Convert.ToString(value) ?? "filled"; break;
                    case "DotSize": settings.DotSize = Convert.ToInt32(value); break;
                    case "DotThickness": settings.DotThickness = Convert.ToInt32(value); break;
                    case "DotColor": settings.DotColor = Convert.ToString(value) ?? "#00FF66"; break;
                    case "DotOpacity": settings.DotOpacity = Convert.ToInt32(value); break;
                    case "CrosshairGap": settings.CrosshairGap = Convert.ToInt32(value); break;
                    case "CrosshairLength": settings.CrosshairLength = Convert.ToInt32(value); break;
                    case "CrosshairThickness": settings.CrosshairThickness = Convert.ToInt32(value); break;
                    case "CrosshairColor": settings.CrosshairColor = Convert.ToString(value) ?? "#00FF66"; break;
                    case "CrosshairOpacity": settings.CrosshairOpacity = Convert.ToInt32(value); break;
                    case "OutlineThickness": settings.OutlineThickness = Convert.ToInt32(value); break;
                    case "OutlineColor": settings.OutlineColor = Convert.ToString(value) ?? "#000000"; break;
                    case "OutlineOpacity": settings.OutlineOpacity = Convert.ToInt32(value); break;
                }

                settings = settings.Normalized();
                preview.Settings = settings;
                onSettingsChanged(settings);
                ApplySettings(settings);
            }

            private void SyncControlFromSettings(Control control)
            {
                string? key = control.Tag as string;
                if (key == null) return;

                object value = GetSettingValue(key);
                switch (control)
                {
                    case CheckBox check:
                        check.Checked = Convert.ToBoolean(value);
                        break;
                    case ComboBox combo:
                        combo.SelectedValue = value.ToString();
                        break;
                    case TrackBar track:
                        track.Value = Math.Clamp(Convert.ToInt32(value), track.Minimum, track.Maximum);
                        break;
                    case NumericUpDown number:
                        number.Value = Math.Clamp(Convert.ToInt32(value), (int)number.Minimum, (int)number.Maximum);
                        break;
                    case Button button when key.EndsWith("Color", StringComparison.Ordinal):
                        button.BackColor = GetColorSetting(key);
                        button.ForeColor = GetReadableTextColor(button.BackColor);
                        break;
                }
            }

            private object GetSettingValue(string key) => key switch
            {
                "Enabled" => settings.Enabled,
                "ShowOnlyWhileRightMouseButtonHeld" => settings.ShowOnlyWhileRightMouseButtonHeld,
                "NonAimingOpacity" => settings.NonAimingOpacity,
                "OverallSize" => settings.OverallSize,
                "DotStyle" => settings.DotStyle,
                "DotSize" => settings.DotSize,
                "DotThickness" => settings.DotThickness,
                "DotColor" => settings.DotColor,
                "DotOpacity" => settings.DotOpacity,
                "CrosshairGap" => settings.CrosshairGap,
                "CrosshairLength" => settings.CrosshairLength,
                "CrosshairThickness" => settings.CrosshairThickness,
                "CrosshairColor" => settings.CrosshairColor,
                "CrosshairOpacity" => settings.CrosshairOpacity,
                "OutlineThickness" => settings.OutlineThickness,
                "OutlineColor" => settings.OutlineColor,
                "OutlineOpacity" => settings.OutlineOpacity,
                _ => 0
            };

            private Color GetColorSetting(string key)
            {
                return ColorTranslator.FromHtml(Convert.ToString(GetSettingValue(key)) ?? "#000000");
            }

            private static Color GetReadableTextColor(Color color)
            {
                return ((color.R * 299) + (color.G * 587) + (color.B * 114)) / 1000 > 140 ? Color.Black : Color.White;
            }

            private static IEnumerable<Control> GetAllControls(Control root)
            {
                foreach (Control child in root.Controls)
                {
                    yield return child;
                    foreach (Control nested in GetAllControls(child))
                        yield return nested;
                }
            }
        }

        public class CrosshairPreviewPanel : Panel
        {
            private CrosshairSettings settings = new();

            public CrosshairSettings Settings
            {
                get => settings;
                set
                {
                    settings = value.Normalized();
                    Invalidate();
                }
            }

            public CrosshairPreviewPanel()
            {
                DoubleBuffered = true;
                BackColor = Color.FromArgb(0x18, 0x18, 0x18);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var gridPen = new Pen(Color.FromArgb(45, Color.White), 1);
                e.Graphics.DrawLine(gridPen, Width / 2, 0, Width / 2, Height);
                e.Graphics.DrawLine(gridPen, 0, Height / 2, Width, Height / 2);

                float scale = settings.OverallSize / 100f;
                float centerX = Width / 2f;
                float centerY = Height / 2f;
                DrawPreview(e.Graphics, centerX, centerY, scale);
            }

            private void DrawPreview(Graphics g, float centerX, float centerY, float scale)
            {
                DrawLines(g, centerX, centerY, scale, true);
                DrawLines(g, centerX, centerY, scale, false);
                DrawDot(g, centerX, centerY, scale, true);
                DrawDot(g, centerX, centerY, scale, false);
            }

            private void DrawLines(Graphics g, float centerX, float centerY, float scale, bool outline)
            {
                int alpha = ToAlpha(outline ? settings.OutlineOpacity : settings.CrosshairOpacity);
                int outlineThickness = outline ? settings.OutlineThickness : 0;
                if (alpha <= 0 || (outline && outlineThickness <= 0)) return;

                float gap = settings.CrosshairGap * scale;
                float length = settings.CrosshairLength * scale;
                float thickness = Math.Max(1f, (settings.CrosshairThickness + outlineThickness * 2) * scale);
                string colorHex = outline ? settings.OutlineColor : settings.CrosshairColor;
                using var pen = new Pen(Color.FromArgb(alpha, ColorTranslator.FromHtml(colorHex)), thickness);

                g.DrawLine(pen, centerX - gap - length, centerY, centerX - gap, centerY);
                g.DrawLine(pen, centerX + gap, centerY, centerX + gap + length, centerY);
                g.DrawLine(pen, centerX, centerY - gap - length, centerX, centerY - gap);
                g.DrawLine(pen, centerX, centerY + gap, centerX, centerY + gap + length);
            }

            private void DrawDot(Graphics g, float centerX, float centerY, float scale, bool outline)
            {
                if (settings.DotStyle == "none") return;

                int alpha = ToAlpha(outline ? settings.OutlineOpacity : settings.DotOpacity);
                int outlineThickness = outline ? settings.OutlineThickness : 0;
                if (alpha <= 0 || (outline && outlineThickness <= 0)) return;

                float size = Math.Max(1f, (settings.DotSize + outlineThickness * 2) * scale);
                float thickness = Math.Max(1f, (settings.DotThickness + outlineThickness * 2) * scale);
                var rect = new RectangleF(centerX - size / 2f, centerY - size / 2f, size, size);
                string colorHex = outline ? settings.OutlineColor : settings.DotColor;
                Color color = Color.FromArgb(alpha, ColorTranslator.FromHtml(colorHex));

                if (settings.DotStyle == "filled")
                {
                    using var brush = new SolidBrush(color);
                    g.FillEllipse(brush, rect);
                }
                else
                {
                    using var pen = new Pen(color, thickness);
                    g.DrawEllipse(pen, rect);
                }
            }

            private static int ToAlpha(int opacity)
            {
                return (int)Math.Round(Math.Clamp(opacity, 0, 100) * 2.55);
            }
        }

        private class SupportWeaponGaugeForm : Form
        {
            protected override bool ShowWithoutActivation => true;

            private SupportWeaponAssistSettings settings = new();
            private double progress;
            private double elapsedSeconds;
            private bool showGauge;
            private string message = "";
            private string supportWeaponMode = "Off";
            private readonly Font messageFont = new("맑은 고딕", 10, FontStyle.Bold);

            public SupportWeaponGaugeForm()
            {
                // 지원무기 게이지는 게임 입력을 방해하지 않도록 클릭 통과, 비활성 오버레이로 띄운다.
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
                BackColor = Color.Magenta;
                TransparencyKey = Color.Magenta;
                StartPosition = FormStartPosition.Manual;
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_EX_TRANSPARENT = 0x00000020;
                    const int WS_EX_TOOLWINDOW = 0x00000080;
                    const int WS_EX_NOACTIVATE = 0x08000000;
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                    return cp;
                }
            }

            public void ApplyState(SupportWeaponAssistSettings nextSettings, Point crosshairCenter, double nextProgress, double nextElapsedSeconds, bool nextShowGauge, string nextMessage, string nextSupportWeaponMode)
            {
                SupportWeaponAssistSettings normalized = nextSettings.Normalized();
                double normalizedProgress = Math.Clamp(nextProgress, 0, 1);
                double normalizedElapsedSeconds = Math.Max(0, nextElapsedSeconds);
                string normalizedMessage = nextMessage ?? "";
                string normalizedMode = string.IsNullOrWhiteSpace(nextSupportWeaponMode) ? "Off" : nextSupportWeaponMode;

                // 상시 갱신 OFF에서는 정지 상태의 같은 프레임을 다시 그리지 않아 DWM 합성 부하를 분리해서 시험한다.
                bool visualChanged = !AreGaugeRenderSettingsEqual(settings, normalized)
                    || Math.Abs(progress - normalizedProgress) > 0.0001
                    || Math.Abs(elapsedSeconds - normalizedElapsedSeconds) > 0.0001
                    || showGauge != nextShowGauge
                    || !string.Equals(message, normalizedMessage, StringComparison.Ordinal)
                    || !string.Equals(supportWeaponMode, normalizedMode, StringComparison.Ordinal);

                settings = normalized;
                progress = normalizedProgress;
                elapsedSeconds = normalizedElapsedSeconds;
                showGauge = nextShowGauge;
                message = normalizedMessage;
                supportWeaponMode = normalizedMode;

                int bodyWidth = settings.GaugeVerticalMode ? settings.GaugeHeight : settings.GaugeWidth;
                int bodyHeight = settings.GaugeVerticalMode ? settings.GaugeWidth : settings.GaugeHeight;
                int width = settings.GaugeVerticalMode ? Math.Max(bodyWidth + 28, 58) : Math.Max(bodyWidth + 28, 220);
                int height = settings.GaugeVerticalMode ? Math.Max(bodyHeight + 48, 220) : Math.Max(bodyHeight + 48, 58);
                int offsetX = settings.GaugeVerticalMode ? settings.VerticalGaugeOffsetX : settings.GaugeOffsetX;
                int offsetY = settings.GaugeVerticalMode ? settings.VerticalGaugeOffsetY : settings.GaugeOffsetY;
                Size nextSize = new(width, height);
                Point nextLocation = new(
                    crosshairCenter.X + offsetX - nextSize.Width / 2,
                    crosshairCenter.Y + offsetY - nextSize.Height / 2
                );
                double nextOpacity = settings.GaugeOpacity / 100.0;

                if (Size != nextSize) Size = nextSize;
                if (Location != nextLocation) Location = nextLocation;
                if (Math.Abs(Opacity - nextOpacity) > 0.001) Opacity = nextOpacity;
                if (settings.GaugeAlwaysRefresh || visualChanged)
                    Invalidate();
            }

            private static bool AreGaugeRenderSettingsEqual(SupportWeaponAssistSettings left, SupportWeaponAssistSettings right)
            {
                return left.GaugeOpacity == right.GaugeOpacity
                    && left.GaugeOffsetX == right.GaugeOffsetX
                    && left.GaugeOffsetY == right.GaugeOffsetY
                    && left.GaugeVerticalMode == right.GaugeVerticalMode
                    && left.VerticalGaugeOffsetX == right.VerticalGaugeOffsetX
                    && left.VerticalGaugeOffsetY == right.VerticalGaugeOffsetY
                    && left.GaugeWidth == right.GaugeWidth
                    && left.GaugeHeight == right.GaugeHeight;
            }

            public void EnsureTopMost()
            {
                if (!IsHandleCreated) return;
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                e.Graphics.Clear(Color.Magenta);

                if (!string.IsNullOrWhiteSpace(message))
                    DrawMessage(e.Graphics);

                if (showGauge)
                    DrawGauge(e.Graphics);
            }

            private void DrawMessage(Graphics g)
            {
                var rect = new RectangleF(0, 0, Width, 24);
                using var shadow = new SolidBrush(Color.FromArgb(210, 0, 0, 0));
                using var brush = new SolidBrush(Color.White);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(message, messageFont, shadow, new RectangleF(rect.X + 1, rect.Y + 1, rect.Width, rect.Height), format);
                g.DrawString(message, messageFont, brush, rect, format);
            }

            private void DrawGauge(Graphics g)
            {
                bool vertical = settings.GaugeVerticalMode;
                int gaugeWidth = vertical ? settings.GaugeHeight : settings.GaugeWidth;
                int gaugeHeight = vertical ? settings.GaugeWidth : settings.GaugeHeight;
                int gaugeX = (Width - gaugeWidth) / 2;
                int gaugeY = string.IsNullOrWhiteSpace(message) ? 16 : 30;
                var outer = new Rectangle(gaugeX, gaugeY, gaugeWidth, gaugeHeight);
                using var back = new SolidBrush(GetGaugeBackgroundColor());
                using var border = new Pen(Color.FromArgb(230, 120, 120, 125), 1);
                g.FillRectangle(back, outer);
                g.DrawRectangle(border, outer);

                if (progress <= 0)
                    return;

                using var fill = new SolidBrush(GetGaugeFillColor());
                if (vertical)
                {
                    int fillHeight = (int)Math.Round((outer.Height - 2) * progress);
                    if (fillHeight <= 0) return;
                    g.FillRectangle(fill, outer.X + 1, outer.Bottom - 1 - fillHeight, Math.Max(1, outer.Width - 2), fillHeight);
                }
                else
                {
                    int fillWidth = (int)Math.Round((outer.Width - 2) * progress);
                    if (fillWidth <= 0) return;
                    g.FillRectangle(fill, outer.X + 1, outer.Y + 1, fillWidth, Math.Max(1, outer.Height - 2));
                }
            }

            private Color GetGaugeBackgroundColor()
            {
                // 모드별 배경색을 다르게 해서 게이지 색상만 보기 전에도 현재 기능 상태를 구분할 수 있게 한다.
                return supportWeaponMode switch
                {
                    "AutoFire" => Color.FromArgb(235, 64, 42, 18),
                    "AutoRepeat" => Color.FromArgb(235, 18, 70, 34),
                    "Danger" => Color.FromArgb(235, 18, 48, 70),
                    _ => Color.FromArgb(235, 28, 28, 30)
                };
            }

            private Color GetGaugeFillColor()
            {
                // 자동사격 차지 게이지는 0.5초 이후부터 주의 구간으로 보이도록 노란색으로 전환한다.
                return elapsedSeconds < 0.5
                    ? Color.FromArgb(0, 255, 90)
                    : elapsedSeconds < 2.5
                        ? Color.FromArgb(255, 226, 20)
                        : Color.FromArgb(255, 55, 55);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    messageFont.Dispose();

                base.Dispose(disposing);
            }
        }
        public class CrosshairForm : Form
        {
            protected override bool ShowWithoutActivation => true;

            private CrosshairSettings settings;
            private int displayOpacityPercent = 100;

            public CrosshairForm(CrosshairSettings initialSettings)
            {
                settings = initialSettings.Normalized();
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                ApplySettings(settings, Screen.PrimaryScreen!.Bounds.Location);
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_EX_TRANSPARENT = 0x00000020;
                    const int WS_EX_LAYERED = 0x00080000;
                    const int WS_EX_TOOLWINDOW = 0x00000080;
                    const int WS_EX_NOACTIVATE = 0x08000000;

                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                    return cp;
                }
            }

            public void ApplySettings(CrosshairSettings nextSettings, Point center, int nextDisplayOpacityPercent = 100)
            {
                CrosshairSettings normalized = nextSettings.Normalized();
                int nextDisplayOpacity = Math.Clamp(nextDisplayOpacityPercent, 0, 100);
                int size = GetCanvasSize(normalized);
                Size nextSize = new(size, size);
                Point nextLocation = new(center.X - size / 2, center.Y - size / 2);

                // 조준점은 애니메이션이 없으므로 실제 시각 요소가 바뀐 경우에만 layered window를 다시 그린다.
                bool visualChanged = !AreSettingsEqual(settings, normalized)
                    || displayOpacityPercent != nextDisplayOpacity
                    || Size != nextSize;

                settings = normalized;
                displayOpacityPercent = nextDisplayOpacity;

                if (Size != nextSize)
                    Size = nextSize;

                if (Location != nextLocation)
                    Location = nextLocation;

                if (IsHandleCreated && visualChanged)
                    RenderLayeredWindow();
            }

            private static bool AreSettingsEqual(CrosshairSettings left, CrosshairSettings right)
            {
                return left.Enabled == right.Enabled
                    && left.ShowOnlyWhileRightMouseButtonHeld == right.ShowOnlyWhileRightMouseButtonHeld
                    && left.NonAimingOpacity == right.NonAimingOpacity
                    && left.OverallSize == right.OverallSize
                    && left.DotStyle == right.DotStyle
                    && left.DotSize == right.DotSize
                    && left.DotThickness == right.DotThickness
                    && left.DotColor == right.DotColor
                    && left.DotOpacity == right.DotOpacity
                    && left.CrosshairGap == right.CrosshairGap
                    && left.CrosshairLength == right.CrosshairLength
                    && left.CrosshairThickness == right.CrosshairThickness
                    && left.CrosshairColor == right.CrosshairColor
                    && left.CrosshairOpacity == right.CrosshairOpacity
                    && left.OutlineThickness == right.OutlineThickness
                    && left.OutlineColor == right.OutlineColor
                    && left.OutlineOpacity == right.OutlineOpacity;
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                RenderLayeredWindow();
                EnsureTopMost();
            }

            public void EnsureTopMost()
            {
                if (!IsHandleCreated) return;

                // helper를 게임 실행 중에 다시 켰을 때 조준점 layered window가 게임 뒤로 밀리는 경우가 있어 매 표시마다 topmost 순서를 복구한다.
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }

            private void RenderLayeredWindow()
            {
                if (Width <= 0 || Height <= 0) return;

                using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);

                    float scale = settings.OverallSize / 100f;
                    float centerX = Width / 2f;
                    float centerY = Height / 2f;

                    DrawCrosshairOutline(g, centerX, centerY, scale);
                    DrawCrosshairLines(g, centerX, centerY, scale);
                    DrawCenterDotOutline(g, centerX, centerY, scale);
                    DrawCenterDot(g, centerX, centerY, scale);
                }

                // 컬러키 투명 대신 per-pixel alpha를 사용해 안티앨리어싱 가장자리 색 섞임을 막는다.
                IntPtr screenDC = GetDC(IntPtr.Zero);
                IntPtr memDC = CreateCompatibleDC(screenDC);
                IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                IntPtr oldBitmap = SelectObject(memDC, hBitmap);

                try
                {
                    var size = new SIZE { cx = Width, cy = Height };
                    var topPos = new POINT { x = Left, y = Top };
                    var source = new POINT { x = 0, y = 0 };
                    var blend = new BLENDFUNCTION
                    {
                        BlendOp = AC_SRC_OVER,
                        BlendFlags = 0,
                        SourceConstantAlpha = 255,
                        AlphaFormat = AC_SRC_ALPHA
                    };

                    UpdateLayeredWindow(Handle, screenDC, ref topPos, ref size, memDC, ref source, 0, ref blend, ULW_ALPHA);
                    EnsureTopMost();
                }
                finally
                {
                    SelectObject(memDC, oldBitmap);
                    DeleteObject(hBitmap);
                    DeleteDC(memDC);
                    ReleaseDC(IntPtr.Zero, screenDC);
                }
            }

            private void DrawCrosshairOutline(Graphics g, float centerX, float centerY, float scale)
            {
                int alpha = ToAlpha(settings.OutlineOpacity);
                if (alpha <= 0 || settings.OutlineThickness <= 0) return;

                float gap = settings.CrosshairGap * scale;
                float length = settings.CrosshairLength * scale;
                float thickness = Math.Max(1f, (settings.CrosshairThickness + settings.OutlineThickness * 2) * scale);

                using var pen = new Pen(Color.FromArgb(alpha, ParseColor(settings.OutlineColor)), thickness)
                {
                    StartCap = LineCap.Square,
                    EndCap = LineCap.Square
                };

                g.DrawLine(pen, centerX - gap - length, centerY, centerX - gap, centerY);
                g.DrawLine(pen, centerX + gap, centerY, centerX + gap + length, centerY);
                g.DrawLine(pen, centerX, centerY - gap - length, centerX, centerY - gap);
                g.DrawLine(pen, centerX, centerY + gap, centerX, centerY + gap + length);
            }

            private void DrawCrosshairLines(Graphics g, float centerX, float centerY, float scale)
            {
                int alpha = ToAlpha(settings.CrosshairOpacity);
                if (alpha <= 0) return;

                float gap = settings.CrosshairGap * scale;
                float length = settings.CrosshairLength * scale;
                float thickness = Math.Max(1f, settings.CrosshairThickness * scale);

                using var pen = new Pen(Color.FromArgb(alpha, ParseColor(settings.CrosshairColor)), thickness)
                {
                    StartCap = LineCap.Square,
                    EndCap = LineCap.Square
                };

                g.DrawLine(pen, centerX - gap - length, centerY, centerX - gap, centerY);
                g.DrawLine(pen, centerX + gap, centerY, centerX + gap + length, centerY);
                g.DrawLine(pen, centerX, centerY - gap - length, centerX, centerY - gap);
                g.DrawLine(pen, centerX, centerY + gap, centerX, centerY + gap + length);
            }

            private void DrawCenterDot(Graphics g, float centerX, float centerY, float scale)
            {
                if (settings.DotStyle == "none") return;

                int alpha = ToAlpha(settings.DotOpacity);
                if (alpha <= 0) return;

                float size = Math.Max(1f, settings.DotSize * scale);
                float thickness = Math.Max(1f, settings.DotThickness * scale);
                var rect = new RectangleF(centerX - size / 2f, centerY - size / 2f, size, size);
                Color color = Color.FromArgb(alpha, ParseColor(settings.DotColor));

                if (settings.DotStyle == "filled")
                {
                    using var brush = new SolidBrush(color);
                    g.FillEllipse(brush, rect);
                    return;
                }

                using var pen = new Pen(color, thickness);
                g.DrawEllipse(pen, rect);
            }

            private void DrawCenterDotOutline(Graphics g, float centerX, float centerY, float scale)
            {
                if (settings.DotStyle == "none") return;

                int alpha = ToAlpha(settings.OutlineOpacity);
                if (alpha <= 0 || settings.OutlineThickness <= 0) return;

                float outline = settings.OutlineThickness * scale;
                float size = Math.Max(1f, settings.DotSize * scale + outline * 2);
                float thickness = Math.Max(1f, settings.DotThickness * scale + outline * 2);
                var rect = new RectangleF(centerX - size / 2f, centerY - size / 2f, size, size);
                Color color = Color.FromArgb(alpha, ParseColor(settings.OutlineColor));

                if (settings.DotStyle == "filled")
                {
                    using var brush = new SolidBrush(color);
                    g.FillEllipse(brush, rect);
                    return;
                }

                using var pen = new Pen(color, thickness);
                g.DrawEllipse(pen, rect);
            }

            private static int GetCanvasSize(CrosshairSettings settings)
            {
                int maxRadius = settings.CrosshairGap + settings.CrosshairLength + settings.CrosshairThickness + settings.DotSize + settings.OutlineThickness * 2;
                int scaled = (int)Math.Ceiling(maxRadius * (settings.OverallSize / 100f) * 2 + 32);
                return Math.Clamp(scaled, 64, 512);
            }

            private static Color ParseColor(string hex)
            {
                return ColorTranslator.FromHtml(hex);
            }

            private int ToAlpha(int opacity)
            {
                // 비조준 투명도는 각 레이어 투명도 위에 곱해져 색/레이어 비율은 그대로 유지된다.
                return (int)Math.Round(Math.Clamp(opacity, 0, 100) * Math.Clamp(displayOpacityPercent, 0, 100) * 255d / 10000d);
            }

            private const int ULW_ALPHA = 0x00000002;
            private const byte AC_SRC_OVER = 0x00;
            private const byte AC_SRC_ALPHA = 0x01;
            private static readonly IntPtr HWND_TOPMOST = new(-1);
            private const uint SWP_NOSIZE = 0x0001;
            private const uint SWP_NOMOVE = 0x0002;
            private const uint SWP_NOACTIVATE = 0x0010;
            private const uint SWP_SHOWWINDOW = 0x0040;

            [StructLayout(LayoutKind.Sequential)]
            private struct POINT { public int x; public int y; }

            [StructLayout(LayoutKind.Sequential)]
            private struct SIZE { public int cx; public int cy; }

            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            private struct BLENDFUNCTION
            {
                public byte BlendOp;
                public byte BlendFlags;
                public byte SourceConstantAlpha;
                public byte AlphaFormat;
            }

            [DllImport("user32.dll")]
            private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

            [DllImport("user32.dll")]
            private static extern IntPtr GetDC(IntPtr hWnd);

            [DllImport("user32.dll")]
            private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

            [DllImport("gdi32.dll")]
            private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

            [DllImport("gdi32.dll")]
            private static extern bool DeleteDC(IntPtr hdc);

            [DllImport("gdi32.dll")]
            private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

            [DllImport("gdi32.dll")]
            private static extern bool DeleteObject(IntPtr hObject);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool UpdateLayeredWindow(
                IntPtr hwnd,
                IntPtr hdcDst,
                ref POINT pptDst,
                ref SIZE psize,
                IntPtr hdcSrc,
                ref POINT pptSrc,
                int crKey,
                ref BLENDFUNCTION pblend,
                int dwFlags);
        }

        public class OverlayForm : Form
        {
            protected override bool ShowWithoutActivation => true;

            private const int BaseOverlaySize = 515;
            private const float BasePlacementRadius = 163.75f;
            private const float BaseDeadZoneRadius = 70f;
            private const float BaseLargeIconSize = 256f / 3f;
            private const float BaseSmallIconSize = 256f / 4f;
            private const float BaseMouseMultiplier = 1.8f;

            private readonly float overlayScale;
            private readonly int overlaySize;
            private readonly float placementRadius;
            private readonly float deadZoneRadius;
            private readonly float mouseMultiplier;
            private readonly float cursorRadius;

            private CancellationTokenSource? loopCts;
            private bool isUpdating = false;
            private int selectedSlot = -1;

            private string[] slotNames;
            private string[] lastNames;
            private int slotCount;
            private Image[] slotImages;
            private float iconSize;

            private Bitmap[]? selectionBuffers;
            private Bitmap staticBuffer;
            private Bitmap backBuffer;
            private readonly Graphics staticBufferGraphics;
            private readonly Graphics backBufferGraphics;

            private IntPtr hBitmap;
            private IntPtr pBits;
            private IntPtr memDC;
            private IntPtr oldBitmap;

            private readonly int centerX, centerY;
            private float virtualX, virtualY;
            private Point currentMousePos;
            private Point lastRawPos;

            private readonly SolidBrush evenBrush = new SolidBrush(Color.FromArgb(153, 0x15, 0x15, 0x15));
            private readonly SolidBrush oddBrush = new SolidBrush(Color.FromArgb(153, 0x33, 0x33, 0x33));
            private readonly SolidBrush lastBrush = new SolidBrush(Color.FromArgb(153, 0x22, 0x22, 0x22));
            private readonly SolidBrush selectionBrush = new SolidBrush(Color.FromArgb(153, 180, 180, 180));
            private readonly Font textFont;
            private readonly Pen linePen;

            public string? Selected
            {
                get
                {
                    if (slotNames != null && selectedSlot >= 0 && selectedSlot < slotNames.Length)
                        return slotNames[selectedSlot];
                    return null;
                }
            }

            public OverlayForm(string[] names, Image[] images)
            {
                Load += OverlayForm_Load;

                var screen = Screen.PrimaryScreen!.Bounds;
                int screenCenterX = screen.Width / 2;
                int screenCenterY = screen.Height / 2;

                overlayScale = (float)Math.Min((double)screen.Width / BaseReferenceWidth, (double)screen.Height / BaseReferenceHeight);
                overlaySize = Math.Max(1, (int)Math.Round(BaseOverlaySize * overlayScale));
                placementRadius = BasePlacementRadius * overlayScale;
                deadZoneRadius = BaseDeadZoneRadius * overlayScale;
                mouseMultiplier = BaseMouseMultiplier * overlayScale;
                cursorRadius = 7f * overlayScale;

                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
                ShowInTaskbar = false;
                Width = overlaySize;
                Height = overlaySize;
                Left = screenCenterX - Width / 2;
                Top = screenCenterY - Height / 2;

                slotNames = names;
                lastNames = names;
                slotCount = slotNames.Length;
                slotImages = images;
                iconSize = (slotCount > 8 ? BaseSmallIconSize : BaseLargeIconSize) * overlayScale;

                centerX = overlaySize / 2;
                centerY = overlaySize / 2;
                currentMousePos = new Point(centerX, centerY);
                virtualX = centerX;
                virtualY = centerY;

                using (Graphics g = this.CreateGraphics()) { textFont = new Font("Malgun Gothic", 12 / (g.DpiX / 96.0f), FontStyle.Bold); }
                linePen = new Pen(Color.White, Math.Max(1f, 3f * overlayScale)) { StartCap = LineCap.Round, EndCap = LineCap.Round };

                staticBuffer = new Bitmap(overlaySize, overlaySize, PixelFormat.Format32bppPArgb);
                staticBufferGraphics = Graphics.FromImage(staticBuffer);
                staticBufferGraphics.SmoothingMode = SmoothingMode.AntiAlias;
                staticBufferGraphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                staticBufferGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                staticBufferGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                IntPtr screenDC = GetDC(IntPtr.Zero);
                memDC = CreateCompatibleDC(screenDC);

                BITMAPINFO bmi = new BITMAPINFO();
                bmi.biSize = Marshal.SizeOf(typeof(BITMAPINFO));
                bmi.biWidth = overlaySize;
                bmi.biHeight = -overlaySize;
                bmi.biPlanes = 1;
                bmi.biBitCount = 32;
                bmi.biCompression = 0;

                hBitmap = CreateDIBSection(memDC, ref bmi, 0, out pBits, IntPtr.Zero, 0);
                oldBitmap = SelectObject(memDC, hBitmap);

                backBuffer = new Bitmap(overlaySize, overlaySize, overlaySize * 4, PixelFormat.Format32bppPArgb, pBits);
                backBufferGraphics = Graphics.FromImage(backBuffer);
                backBufferGraphics.SmoothingMode = SmoothingMode.AntiAlias;
                backBufferGraphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                backBufferGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                backBufferGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                ReleaseDC(IntPtr.Zero, screenDC);
            }

            private void OverlayForm_Load(object? sender, EventArgs e)
            {
                int exStyle = GetWindowLong(Handle, GWL_EXSTYLE);
                SetWindowLong(Handle, GWL_EXSTYLE, exStyle | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);

                RenderStaticBackground();
                RenderOverlay();
            }
            protected override void OnVisibleChanged(EventArgs e)
            {
                base.OnVisibleChanged(e);

                if (this.Visible) StartLoop();
                else StopLoop();
            }

            private void StartLoop()
            {
                StopLoop();

                lastRawPos = Cursor.Position;
                loopCts = new CancellationTokenSource();
                var token = loopCts.Token;

                Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (_isChat || CursorUtil.IsVisible())
                        {
                            this.BeginInvoke(new Action(() => this.Hide()));
                            break;
                        }

                        Point currentRawPos = Cursor.Position;
                        int dx = currentRawPos.X - lastRawPos.X;
                        int dy = currentRawPos.Y - lastRawPos.Y;

                        if (Math.Abs(dx) < 100 && Math.Abs(dy) < 100 && (dx != 0 || dy != 0))
                        {
                            UpdateVirtualMouse(dx, dy);

                            if (!isUpdating)
                            {
                                isUpdating = true;
                                this.BeginInvoke(new Action(() =>
                                {
                                    try
                                    {
                                        if (this.IsDisposed || !this.Visible) return;
                                        UpdateSelectionFromPos((int)virtualX, (int)virtualY);
                                    }
                                    finally
                                    {
                                        isUpdating = false;
                                    }
                                }));
                            }
                        }

                        lastRawPos = currentRawPos;
                        await Task.Delay(16, token);
                    }
                }, token);
            }

            private void StopLoop()
            {
                loopCts?.Cancel();
                loopCts?.Dispose();
                loopCts = null;
            }

            public void UpdateSlot(string[] names, Image[] images)
            {
                slotNames = names;
                slotCount = names.Length;
                slotImages = images;
                iconSize = (slotCount > 8 ? BaseSmallIconSize : BaseLargeIconSize) * overlayScale;

                currentMousePos = new Point(centerX, centerY);
                virtualX = centerX;
                virtualY = centerY;
                selectedSlot = -1;

                if (!lastNames.SequenceEqual(names))
                {
                    lastNames = names;
                    RenderStaticBackground();
                }

                RenderOverlay();
            }

            private void UpdateVirtualMouse(int dx, int dy)
            {
                virtualX += dx * mouseMultiplier;
                virtualY += dy * mouseMultiplier;

                float vDx = virtualX - centerX;
                float vDy = virtualY - centerY;
                double distSq = vDx * vDx + vDy * vDy;

                if (distSq > placementRadius * placementRadius)
                {
                    float dist = (float)Math.Sqrt(distSq);
                    virtualX = centerX + (vDx / dist) * placementRadius;
                    virtualY = centerY + (vDy / dist) * placementRadius;
                }
            }

            private void UpdateSelectionFromPos(int x, int y)
            {
                float dx = x - centerX;
                float dy = y - centerY;
                float distanceSq = dx * dx + dy * dy;
                int newSelectedSlot = -1;

                if (slotCount > 0 && distanceSq > deadZoneRadius * deadZoneRadius)
                {
                    const double TAU = Math.PI * 2.0;
                    double angle = Math.Atan2(dy, dx) + (Math.PI / 2.0) + (TAU / (slotCount * 2.0));
                    angle = (angle % TAU + TAU) % TAU;
                    newSelectedSlot = (int)(angle / (TAU / slotCount)) % slotCount;
                }

                selectedSlot = newSelectedSlot;
                currentMousePos = new Point(x, y);

                RenderOverlay();
            }

            private void RenderStaticBackground()
            {
                var g = staticBufferGraphics;
                g.Clear(Color.Transparent);

                using (GraphicsPath outerPath = new GraphicsPath())
                using (GraphicsPath innerPath = new GraphicsPath())
                {
                    outerPath.AddEllipse(0, 0, overlaySize, overlaySize);
                    innerPath.AddEllipse(centerX - deadZoneRadius, centerY - deadZoneRadius, deadZoneRadius * 2, deadZoneRadius * 2);

                    using (Region donutRegion = new Region(outerPath))
                    {
                        donutRegion.Exclude(innerPath);
                        g.Clip = donutRegion;

                        float sectorAngle = 360f / slotCount;
                        float startAngle = -90f - (sectorAngle / 2f);

                        if (selectionBuffers != null)
                            foreach (var bmp in selectionBuffers) bmp?.Dispose();
                        selectionBuffers = new Bitmap[slotCount];

                        for (int i = 0; i < slotCount; i++)
                        {
                            float currentStartAngle = startAngle + (sectorAngle * i);

                            var sectorBrush = (slotCount % 2 == 1 && i == slotCount - 1) ? lastBrush : (i % 2 == 0 ? evenBrush : oddBrush);
                            g.FillPie(sectorBrush, 0, 0, overlaySize, overlaySize, currentStartAngle, sectorAngle);

                            var bmp = new Bitmap(overlaySize, overlaySize, PixelFormat.Format32bppPArgb);
                            using (var sg = Graphics.FromImage(bmp))
                            {
                                sg.SmoothingMode = SmoothingMode.AntiAlias;
                                sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                sg.CompositingMode = CompositingMode.SourceCopy;
                                sg.Clip = donutRegion;
                                sg.FillPie(selectionBrush, 0, 0, overlaySize, overlaySize, currentStartAngle, sectorAngle);
                            }
                            selectionBuffers[i] = bmp;
                        }
                    }
                }

                g.ResetClip();

                double sectorAngleRad = (Math.PI * 2.0) / slotCount;
                double startAngleRad = -Math.PI / 2.0 - (sectorAngleRad / 2.0);

                for (int i = 0; i < slotCount; i++)
                {
                    double currentIconRad = startAngleRad + (sectorAngleRad * i) + (sectorAngleRad / 2.0);
                    float x = centerX + (float)(placementRadius * Math.Cos(currentIconRad)) - iconSize / 2;
                    float y = centerY + (float)(placementRadius * Math.Sin(currentIconRad)) - iconSize / 2;

                    if (slotImages[i] != null)
                        g.DrawImage(slotImages[i], x, y, iconSize, iconSize);
                    else if (!string.IsNullOrEmpty(slotNames[i]))
                    {
                        SizeF textSize = g.MeasureString(slotNames[i], textFont);
                        g.DrawString(slotNames[i], textFont, Brushes.White,
                            x + (iconSize - textSize.Width) / 2,
                            y + (iconSize - textSize.Height) / 2);
                    }
                }
            }

            private void RenderOverlay()
            {
                var g = backBufferGraphics;

                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(staticBuffer, 0, 0);
                g.CompositingMode = CompositingMode.SourceOver;

                if (selectionBuffers != null && selectedSlot >= 0 && selectedSlot < slotCount)
                    g.DrawImageUnscaled(selectionBuffers[selectedSlot], 0, 0);

                g.DrawLine(linePen, centerX, centerY, currentMousePos.X, currentMousePos.Y);

                g.FillEllipse(Brushes.White,
                    currentMousePos.X - cursorRadius,
                    currentMousePos.Y - cursorRadius,
                    cursorRadius * 2, cursorRadius * 2);

                ApplyLayeredWindow();
            }

            private void ApplyLayeredWindow()
            {
                IntPtr screenDC = GetDC(IntPtr.Zero);

                try
                {
                    SIZE size = new SIZE(overlaySize, overlaySize);
                    POINT pointSource = new POINT(0, 0);
                    POINT topPos = new POINT(this.Left, this.Top);

                    BLENDFUNCTION blend = new BLENDFUNCTION
                    {
                        BlendOp = AC_SRC_OVER,
                        BlendFlags = 0,
                        SourceConstantAlpha = 255,
                        AlphaFormat = AC_SRC_ALPHA
                    };

                    UpdateLayeredWindow(Handle, screenDC, ref topPos, ref size, memDC, ref pointSource, 0, ref blend, ULW_ALPHA);
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, screenDC);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    StopLoop();

                    staticBufferGraphics?.Dispose();
                    staticBuffer?.Dispose();
                    backBufferGraphics?.Dispose();
                    backBuffer?.Dispose();

                    if (oldBitmap != IntPtr.Zero) SelectObject(memDC, oldBitmap);
                    if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                    if (memDC != IntPtr.Zero) DeleteDC(memDC);

                    evenBrush?.Dispose();
                    oddBrush?.Dispose();
                    lastBrush?.Dispose();
                    selectionBrush?.Dispose();
                    textFont?.Dispose();
                    linePen?.Dispose();

                    if (selectionBuffers != null)
                        foreach (var bmp in selectionBuffers) bmp?.Dispose();
                }
                base.Dispose(disposing);
            }

            #region WinAPI
            private const int WS_EX_TRANSPARENT = 0x00000020;
            private const int WS_EX_LAYERED = 0x00080000;
            private const int WS_EX_NOACTIVATE = 0x08000000;
            private const int WS_EX_TOOLWINDOW = 0x00000080;
            private const int GWL_EXSTYLE = -20;
            private const byte AC_SRC_OVER = 0x00;
            private const int ULW_ALPHA = 0x02;
            private const byte AC_SRC_ALPHA = 0x01;

            [StructLayout(LayoutKind.Sequential)]
            private struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

            [StructLayout(LayoutKind.Sequential)]
            private struct SIZE { public int cx, cy; public SIZE(int x, int y) { cx = x; cy = y; } }

            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            private struct BLENDFUNCTION
            {
                public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct BITMAPINFO
            {
                public int biSize;
                public int biWidth;
                public int biHeight;
                public short biPlanes;
                public short biBitCount;
                public int biCompression;
                public int biSizeImage;
                public int biXPelsPerMeter;
                public int biYPelsPerMeter;
                public int biClrUsed;
                public int biClrImportant;
            }

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
                IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

            [DllImport("gdi32.dll", SetLastError = true)]
            private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern IntPtr GetDC(IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

            [DllImport("gdi32.dll", SetLastError = true)]
            private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

            [DllImport("gdi32.dll", SetLastError = true)]
            private static extern bool DeleteDC(IntPtr hdc);

            [DllImport("gdi32.dll", SetLastError = true)]
            private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

            [DllImport("gdi32.dll", SetLastError = true)]
            private static extern bool DeleteObject(IntPtr hObject);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
            #endregion
        }

        public static class GamepadReader
        {
            private const string MappingUrl = "https://raw.githubusercontent.com/mdqinc/SDL_GameControllerDB/master/gamecontrollerdb.txt";
            private static IntPtr activeController = IntPtr.Zero;
            private static readonly Dictionary<int, IntPtr> connectedPads = new();
            private static HashSet<PadButton> lastButtons = new(), currentButtonsSet = new();
            private static float rightX, rightY;

            public class PadEvent
            {
                public PadButton Button;
                public bool Pressed;
            }

            public static async Task InitializeAsync()
            {
                SDL.SDL_SetHint("SDL_GAMECONTROLLER_ALLOW_BACKGROUND_EVENTS", "1");
                SDL.SDL_SetHint("SDL_GAMECONTROLLER_IGNORE_DEVICES", "");
                SDL.SDL_Init(SDL.SDL_INIT_GAMECONTROLLER | SDL.SDL_INIT_JOYSTICK | SDL.SDL_INIT_VIDEO);

                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                    string db = await client.GetStringAsync(MappingUrl);
                    var lines = db.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                    foreach (var line in lines)
                    {
                        if (!line.StartsWith("#") && !string.IsNullOrWhiteSpace(line))
                            SDL.SDL_GameControllerAddMapping(line);
                    }

                    for (int i = 0; i < SDL.SDL_NumJoysticks(); i++)
                    {
                        ForceRefreshController(i);
                    }
                }
                catch { }
            }

            private static void UpdateCurrentButtons()
            {
                currentButtonsSet.Clear();

                while (SDL.SDL_PollEvent(out var ev) != 0)
                {
                    switch (ev.type)
                    {
                        case SDL.SDL_EventType.SDL_CONTROLLERDEVICEADDED:
                            ForceRefreshController(ev.cdevice.which);
                            break;
                        case SDL.SDL_EventType.SDL_CONTROLLERDEVICEREMOVED:
                            RemoveController(ev.cdevice.which);
                            break;
                    }
                }

                foreach (var entry in connectedPads)
                {
                    IntPtr pad = entry.Value;
                    for (byte i = 0; i < (byte)SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_MAX; i++)
                    {
                        if (SDL.SDL_GameControllerGetButton(pad, (SDL.SDL_GameControllerButton)i) == 1)
                        {
                            activeController = pad;
                            break;
                        }
                    }
                }

                if (activeController == IntPtr.Zero) return;

                void Map(SDL.SDL_GameControllerButton s, PadButton p)
                {
                    if (SDL.SDL_GameControllerGetButton(activeController, s) == 1) currentButtonsSet.Add(p);
                }

                Map(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_A, PadButton.PadA);
                Map(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_B, PadButton.PadB);
                Map(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_X, PadButton.PadX);
                Map(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_Y, PadButton.PadY);
                Map(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_LEFTSHOULDER, PadButton.L1);
                Map(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_RIGHTSHOULDER, PadButton.R1);
                Map(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_LEFTSTICK, PadButton.L3);
                Map(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_RIGHTSTICK, PadButton.R3);
                Map(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_START, PadButton.PadStart);
                Map(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_BACK, PadButton.PadBack);
                Map(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_UP, PadButton.DUp);
                Map(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_DOWN, PadButton.DDown);
                Map(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_LEFT, PadButton.DLeft);
                Map(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_RIGHT, PadButton.DRight);

                if (SDL.SDL_GameControllerGetAxis(activeController, SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_TRIGGERLEFT) > 8000) currentButtonsSet.Add(PadButton.L2);
                if (SDL.SDL_GameControllerGetAxis(activeController, SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_TRIGGERRIGHT) > 8000) currentButtonsSet.Add(PadButton.R2);

                rightX = SDL.SDL_GameControllerGetAxis(activeController, SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_RIGHTX) / 32767f;
                rightY = SDL.SDL_GameControllerGetAxis(activeController, SDL.SDL_GameControllerAxis.SDL_CONTROLLER_AXIS_RIGHTY) / 32767f;
            }

            private static void ForceRefreshController(int index)
            {
                if (SDL.SDL_IsGameController(index) == SDL.SDL_bool.SDL_TRUE)
                {
                    IntPtr tempPad = SDL.SDL_GameControllerOpen(index);
                    if (tempPad == IntPtr.Zero) return;

                    int instanceId = SDL.SDL_JoystickInstanceID(SDL.SDL_GameControllerGetJoystick(tempPad));

                    if (connectedPads.TryGetValue(instanceId, out IntPtr existing))
                    {
                        if (activeController == existing) activeController = IntPtr.Zero;
                        SDL.SDL_GameControllerClose(existing);
                        connectedPads.Remove(instanceId);
                    }
                    else
                    {
                        SDL.SDL_GameControllerClose(tempPad);
                    }

                    IntPtr finalPad = SDL.SDL_GameControllerOpen(index);
                    if (finalPad != IntPtr.Zero)
                    {
                        connectedPads[instanceId] = finalPad;
                        if (activeController == IntPtr.Zero) activeController = finalPad;
                    }

                    _isChat = false;
                    _isPad = true;
                }
            }

            private static void RemoveController(int instanceId)
            {
                if (connectedPads.TryGetValue(instanceId, out IntPtr pad))
                {
                    if (activeController == pad) activeController = IntPtr.Zero;
                    SDL.SDL_GameControllerClose(pad);
                    connectedPads.Remove(instanceId);
                    if (activeController == IntPtr.Zero && connectedPads.Count > 0)
                        activeController = connectedPads.Values.First();
                }
            }

            public static List<PadEvent> GetButtonEvents()
            {
                var events = new List<PadEvent>();
                UpdateCurrentButtons();

                foreach (var btn in currentButtonsSet)
                    if (!lastButtons.Contains(btn)) events.Add(new PadEvent { Button = btn, Pressed = true });
                foreach (var btn in lastButtons)
                    if (!currentButtonsSet.Contains(btn)) events.Add(new PadEvent { Button = btn, Pressed = false });

                lastButtons = new HashSet<PadButton>(currentButtonsSet);
                return events;
            }

            public static (float dx, float dy) GetRightStick()
            {
                const float dz = 0.2f;
                float Filter(float v) => Math.Abs(v) < dz ? 0 : (v - (dz * Math.Sign(v))) / (1f - dz);
                return (Filter(rightX), Filter(rightY));
            }

            public static void Quit()
            {
                foreach (var pad in connectedPads.Values) SDL.SDL_GameControllerClose(pad);
                connectedPads.Clear();
                SDL.SDL_Quit();
            }
        }

        public class HangulEngine
        {
            private static readonly string CHOSUNG = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";
            private static readonly string JUNGSUNG = "ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ";
            private static readonly string JONGSUNG = " ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ";

            private static readonly Dictionary<string, char> COMPLEX_VOWELS = new()
            {
                {"ㅗㅏ", 'ㅘ'}, {"ㅗㅐ", 'ㅙ'}, {"ㅗㅣ", 'ㅚ'},
                {"ㅜㅓ", 'ㅝ'}, {"ㅜㅔ", 'ㅞ'}, {"ㅜㅣ", 'ㅟ'}, {"ㅡㅣ", 'ㅢ'}
            };

            private static readonly Dictionary<string, char> COMPLEX_JONGS = new()
            {
                {"ㄱㅅ", 'ㄳ'}, {"ㄴㅈ", 'ㄵ'}, {"ㄴㅎ", 'ㄶ'}, {"ㄹㄱ", 'ㄺ'},
                {"ㄹㅁ", 'ㄻ'}, {"ㄹㅂ", 'ㄼ'}, {"ㄹㅅ", 'ㄽ'}, {"ㄹㅌ", 'ㄾ'},
                {"ㄹㅍ", 'ㄿ'}, {"ㄹㅎ", 'ㅀ'}, {"ㅂㅅ", 'ㅄ'}
            };

            private static readonly Dictionary<string, char> KOR_MAP = new()
            {
                { "r", 'ㄱ' }, { "s", 'ㄴ' }, { "e", 'ㄷ' }, { "f", 'ㄹ' },
                { "a", 'ㅁ' }, { "q", 'ㅂ' }, { "t", 'ㅅ' }, { "d", 'ㅇ' },
                { "w", 'ㅈ' }, { "c", 'ㅊ' }, { "z", 'ㅋ' }, { "x", 'ㅌ' },
                { "v", 'ㅍ' }, { "g", 'ㅎ' }, { "k", 'ㅏ' }, { "o", 'ㅐ' },
                { "i", 'ㅑ' }, { "j", 'ㅓ' }, { "p", 'ㅔ' }, { "u", 'ㅕ' },
                { "h", 'ㅗ' }, { "y", 'ㅛ' }, { "n", 'ㅜ' }, { "b", 'ㅠ' },
                { "m", 'ㅡ' }, { "l", 'ㅣ' }
            };

            private List<char> history = new();
            private string fixedText = "";

            public bool ProcessInput(uint vkCode, bool isShift)
            {
                string k = ((Keys)vkCode).ToString().ToLower();
                char jamo = '\0';

                if (isShift)
                {
                    jamo = k switch
                    {
                        "r" => 'ㄲ',
                        "e" => 'ㄸ',
                        "q" => 'ㅃ',
                        "t" => 'ㅆ',
                        "w" => 'ㅉ',
                        "o" => 'ㅒ',
                        "p" => 'ㅖ',
                        _ => '\0'
                    };
                }

                if (jamo == '\0') KOR_MAP.TryGetValue(k, out jamo);
                if (jamo == '\0') return false;

                Add(jamo);
                return true;
            }

            public void Add(char jamo)
            {
                history.Add(jamo);

                string composed = Compose(history);

                if (composed.Length >= 2)
                {
                    int removeCount = FindRemoveCount(composed);
                    if (removeCount > 0)
                    {
                        fixedText += composed[0];
                        history.RemoveRange(0, removeCount);
                    }
                }
            }

            private int FindRemoveCount(string composed)
            {
                int limit = Math.Min(history.Count, 6);
                for (int i = 1; i <= limit; i++)
                {
                    if (Compose(history.Skip(i).ToList()) == composed.Substring(1))
                        return i;
                }
                return 0;
            }

            public void Backspace()
            {
                if (history.Count > 0)
                {
                    history.RemoveAt(history.Count - 1);
                }
                else if (fixedText.Length > 0)
                {
                    fixedText = fixedText.Substring(0, fixedText.Length - 1);
                }
            }

            public void Flush()
            {
                fixedText += Compose(history);
                history.Clear();
            }

            public void Clear()
            {
                history.Clear();
                fixedText = "";
            }

            public string GetCurrentText() => fixedText + Compose(history);

            public bool IsComposing() => history.Count > 0;

            private string Compose(List<char> input)
            {
                if (input.Count == 0) return "";

                StringBuilder result = new StringBuilder();
                int cho = -1, jung = -1, jong = 0;

                foreach (var c in input)
                {
                    int cCho = CHOSUNG.IndexOf(c);
                    int cJung = JUNGSUNG.IndexOf(c);

                    if (cJung != -1)
                    {
                        ProcessVowel(c, cJung, ref cho, ref jung, ref jong, result);
                    }
                    else if (cCho != -1)
                    {
                        ProcessConsonant(c, cCho, ref cho, ref jung, ref jong, result);
                    }
                    else
                    {
                        if (cho != -1) result.Append(Assemble(cho, jung, jong));
                        result.Append(c);
                        cho = -1; jung = -1; jong = 0;
                    }
                }

                if (cho != -1)
                {
                    if (jung != -1) result.Append(Assemble(cho, jung, jong));
                    else result.Append(CHOSUNG[cho]);
                }

                return NormalizeDoubleFinalSyllables(result.ToString());
            }

            private string NormalizeDoubleFinalSyllables(string text)
            {
                if (text.Length < 2) return text;

                StringBuilder normalized = new(text.Length);
                foreach (char c in text)
                {
                    if ((c == 'ㄲ' || c == 'ㅆ') && normalized.Length > 0)
                    {
                        int lastIndex = normalized.Length - 1;
                        char previous = normalized[lastIndex];
                        int syllable = previous - 0xAC00;
                        int jong = JONGSUNG.IndexOf(c);

                        // ㄲ/ㅆ이 독립 자모로 밀려난 경우, 직전 완성형 한글에 받침이 없으면 종성으로 다시 붙인다.
                        if (jong > 0 && syllable >= 0 && syllable < 11172 && syllable % 28 == 0)
                        {
                            normalized[lastIndex] = (char)(previous + jong);
                            continue;
                        }
                    }

                    normalized.Append(c);
                }

                return normalized.ToString();
            }

            private void ProcessVowel(char c, int cJung, ref int cho, ref int jung, ref int jong, StringBuilder result)
            {
                if (cho != -1 && jung == -1)
                {
                    jung = cJung;
                    return;
                }

                if (cho != -1 && jung != -1 && jong == 0)
                {
                    if (COMPLEX_VOWELS.TryGetValue($"{JUNGSUNG[jung]}{c}", out char v))
                    {
                        jung = JUNGSUNG.IndexOf(v);
                    }
                    else
                    {
                        result.Append(Assemble(cho, jung, 0));
                        cho = -1; jung = cJung;
                    }
                    return;
                }

                if (cho != -1 && jung != -1 && jong != 0)
                {
                    HandleDokkaebibull(cJung, ref cho, ref jung, ref jong, result);
                    return;
                }

                if (cho != -1) result.Append(Assemble(cho, jung, jong));
                result.Append(c);
                cho = -1; jung = -1; jong = 0;
            }

            private void HandleDokkaebibull(int cJung, ref int cho, ref int jung, ref int jong, StringBuilder result)
            {
                string jongStr = JONGSUNG[jong].ToString();
                var complexPair = COMPLEX_JONGS.FirstOrDefault(p => p.Value.ToString() == jongStr);

                if (!complexPair.Equals(default(KeyValuePair<string, char>)))
                {
                    result.Append(Assemble(cho, jung, JONGSUNG.IndexOf(complexPair.Key[0])));
                    cho = CHOSUNG.IndexOf(complexPair.Key[1]);
                }
                else
                {
                    result.Append(Assemble(cho, jung, 0));
                    cho = CHOSUNG.IndexOf(JONGSUNG[jong]);
                }
                jung = cJung;
                jong = 0;
            }

            private void ProcessConsonant(char c, int cCho, ref int cho, ref int jung, ref int jong, StringBuilder result)
            {
                if (cho == -1)
                {
                    cho = cCho;
                }
                else if (jung == -1)
                {
                    result.Append(CHOSUNG[cho]);
                    cho = cCho;
                }
                else if (jong == 0)
                {
                    int j = GetJongIndex(c);
                    if (j != -1) jong = j;
                    else { result.Append(Assemble(cho, jung, 0)); cho = cCho; jung = -1; }
                }
                else
                {
                    if (COMPLEX_JONGS.TryGetValue($"{JONGSUNG[jong]}{c}", out char j))
                    {
                        jong = GetJongIndex(j);
                    }
                    else
                    {
                        result.Append(Assemble(cho, jung, jong));
                        cho = cCho; jung = -1; jong = 0;
                    }
                }
            }

            private static int GetJongIndex(char c)
            {
                // Shift 조합으로 들어오는 ㄲ/ㅆ은 종성에도 존재하므로, 검색 오작동 없이 받침으로 먼저 붙인다.
                return c switch
                {
                    'ㄲ' => 2,
                    'ㅆ' => 20,
                    _ => JONGSUNG.IndexOf(c)
                };
            }

            private char Assemble(int cho, int jung, int jong)
            {
                return (char)(0xAC00 + (cho * 21 * 28) + (jung * 28) + jong);
            }
        }

        public class InputHookManager : IDisposable
        {
            private const int WH_KEYBOARD_LL = 13;
            private const int WH_MOUSE_LL = 14;

            public event EventHandler<InputEventArgs>? OnInputEvent;
            public Func<uint, bool, bool>? TryRouteEditorKeyboardInput { get; set; }
            private readonly Dictionary<uint, bool> keyStates = new();
            private static readonly Dictionary<uint, long> lastTicks = new();

            private IntPtr keyboardHook = IntPtr.Zero;
            private IntPtr mouseHook = IntPtr.Zero;
            private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
            private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
            private readonly LowLevelKeyboardProc keyboardProc;
            private readonly LowLevelMouseProc mouseProc;

            private HangulEngine engine = new();
            private string lastInjected = "";
            private bool isHangulMode = true;
            private bool isProcessing = false;
            private bool isStratagemComboHeld = false;
            private readonly Dictionary<uint, uint> activeStratagemComboKeys = new();
            // 한글 조합 중 Shift를 숨긴 채 직접 입력한 특수문자의 KeyUp도 게임에 중복 전달되지 않게 추적한다.
            private readonly HashSet<uint> injectedShiftSymbolKeys = new();
            public bool IsInstalled => keyboardHook != IntPtr.Zero && mouseHook != IntPtr.Zero;
            public string InstallationError { get; private set; } = "";

            public InputHookManager()
            {
                keyboardProc = KeyboardHookCallback;
                mouseProc = MouseHookCallback;

                // 전역 훅이 다른 프로세스 입력에서도 안정적으로 콜백되도록 현재 실행 모듈 핸들을 명시한다.
                IntPtr moduleHandle = GetModuleHandle(null);
                int moduleError = moduleHandle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;

                keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, moduleHandle, 0);
                int keyboardError = keyboardHook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, moduleHandle, 0);
                int mouseError = mouseHook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;

                if (!IsInstalled)
                {
                    InstallationError = $"module={moduleError}, keyboard={keyboardError}, mouse={mouseError}";
                    if (keyboardHook != IntPtr.Zero)
                    {
                        UnhookWindowsHookEx(keyboardHook);
                        keyboardHook = IntPtr.Zero;
                    }
                    if (mouseHook != IntPtr.Zero)
                    {
                        UnhookWindowsHookEx(mouseHook);
                        mouseHook = IntPtr.Zero;
                    }
                }
            }

            private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
            {
                if (nCode >= 0 && !isProcessing)
                {
                    uint vkCode = (uint)Marshal.ReadInt32(lParam);
                    uint flags = (uint)Marshal.ReadInt32(lParam, 8);
                    bool isDown = (wParam == (IntPtr)0x0100 || wParam == (IntPtr)0x0104);
                    bool isInjected = (flags & 0x10) != 0;

                    if (TryRouteEditorKeyboardInput?.Invoke(vkCode, isDown) == true)
                        return (IntPtr)1;

                    if (IsGameActive() && !_isPad)
                    {
                        if (isDown)
                        {
                            if (vkCode == (uint)Keys.HangulMode)
                            {
                                if (_isChat) isHangulMode = !isHangulMode;

                                if (!isHangulMode && engine.IsComposing())
                                {
                                    engine.Flush();
                                    ExecuteInjectDiff(lastInjected, engine.GetCurrentText());
                                    ResetHangulState();
                                }

                                return (IntPtr)1;
                            }

                            if (vkCode == _chatKey)
                            {
                                foreach (var mouse in _mouseKey)
                                {
                                    if ((GetAsyncKeyState((int)mouse.Key) & 0x8000) != 0)
                                    {
                                        if (mouse.Value.Trigger.Contains("Hold"))
                                        {
                                            return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
                                        }
                                        else if (mouse.Value.Trigger.Contains("LongPress"))
                                        {
                                            if (lastTicks.TryGetValue(mouse.Key, out long t) && t != 0)
                                            {
                                                if ((Environment.TickCount64 - t) >= mouse.Value.Threshold)
                                                {
                                                    _isChat = false;
                                                    ResetHangulState();
                                                    lastTicks[mouse.Key] = 0;
                                                }
                                            }
                                        }
                                    }
                                }

                                if (_chatKey == (uint)Keys.Enter) _isChat = !_isChat;
                                else _isChat = true;
                            }
                            else if (vkCode == (uint)Keys.Escape || vkCode == (uint)Keys.Enter)
                            {
                                _isChat = false;
                            }
                        }

                        if (_isChat)
                        {
                            ReleaseActiveStratagemComboKeys();
                            isStratagemComboHeld = false;
                            isProcessing = true;
                            bool handled = ProcessHangulBypass(vkCode, isDown);
                            isProcessing = false;
                            if (handled) return (IntPtr)1;
                        }
                        else
                        {
                            ResetHangulState();
                            if (TryHandleStratagemComboInput(vkCode, isDown))
                                return (IntPtr)1;
                        }
                    }

                    if (HasStateChanged(vkCode, isDown))
                    {
                        // SendInput으로 주입된 키와 사람이 직접 누른 키를 구분해 ESC 취소 같은 사용자 입력 전용 동작에만 반응하게 한다.
                        OnInputEvent?.Invoke(this, new InputEventArgs(vkCode, isDown, isInjected));
                    }
                }
                return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
            }

            private bool TryHandleStratagemComboInput(uint vkCode, bool isDown)
            {
                if (_stratagemComboKey == 0)
                    return false;

                // 조합키 자체는 게임으로 넘기지 않는다. CapsLock 같은 토글 키의 부작용을 막기 위함이다.
                if (vkCode == _stratagemComboKey)
                {
                    isStratagemComboHeld = isDown;
                    if (!isDown)
                        ReleaseActiveStratagemComboKeys();

                    return true;
                }

                if (!TryGetStratagemComboTarget(vkCode, out uint targetVk))
                    return false;

                if (isDown)
                {
                    if (!isStratagemComboHeld)
                        return false;

                    // 키 반복 이벤트로 같은 방향키 down 입력이 중복 주입되지 않도록 추적한다.
                    if (activeStratagemComboKeys.ContainsKey(vkCode))
                        return true;

                    activeStratagemComboKeys[vkCode] = targetVk;
                    InjectVirtualKey(targetVk, true);
                    return true;
                }

                if (activeStratagemComboKeys.TryGetValue(vkCode, out uint activeTargetVk))
                {
                    if (activeTargetVk != 0)
                        InjectVirtualKey(activeTargetVk, false);

                    activeStratagemComboKeys.Remove(vkCode);
                    return true;
                }

                return false;
            }

            private static bool TryGetStratagemComboTarget(uint sourceVk, out uint targetVk)
            {
                targetVk = 0;

                string? direction = sourceVk switch
                {
                    (uint)Keys.W => "up",
                    (uint)Keys.A => "left",
                    (uint)Keys.S => "down",
                    (uint)Keys.D => "right",
                    _ => null
                };

                if (direction == null)
                    return false;

                if (!TryGetEffectiveStratagemKey(direction, out targetVk))
                    return false;

                return targetVk > 0x06;
            }

            private void ReleaseActiveStratagemComboKeys()
            {
                // 조합키를 먼저 떼거나 채팅으로 전환되어도 주입한 방향키가 눌린 채 남지 않게 한다.
                foreach (var sourceVk in activeStratagemComboKeys.Keys.ToList())
                {
                    uint targetVk = activeStratagemComboKeys[sourceVk];
                    if (targetVk != 0)
                    {
                        InjectVirtualKey(targetVk, false);
                        activeStratagemComboKeys[sourceVk] = 0;
                    }
                }
            }

            private void InjectVirtualKey(uint vkCode, bool isDown)
            {
                isProcessing = true;
                try
                {
                    var input = CreateInput((ushort)vkCode, 0, isDown ? 0u : 0x0002u);
                    SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
                }
                finally
                {
                    isProcessing = false;
                }
            }

            private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
            {
                if (nCode >= 0)
                {
                    uint vk = wParam switch
                    {
                        (IntPtr)0x0201 or (IntPtr)0x0202 => 0x01u,
                        (IntPtr)0x0204 or (IntPtr)0x0205 => 0x02u,
                        (IntPtr)0x0207 or (IntPtr)0x0208 => 0x04u,
                        (IntPtr)0x020B or (IntPtr)0x020C => (uint)((Marshal.ReadInt32(lParam, 8) >> 16) == 1 ? 0x05 : 0x06),
                        _ => 0u
                    };

                    if (vk != 0)
                    {
                        bool isDown = (wParam == (IntPtr)0x0201 || wParam == (IntPtr)0x0204 || wParam == (IntPtr)0x0207 || wParam == (IntPtr)0x020B);

                        if (IsGameActive() && _isChat)
                        {
                            if (vk == 0x02u || _mouseKey.ContainsKey(vk))
                            {
                                if (!_mouseKey.TryGetValue(vk, out var action) && vk == 0x02u)
                                {
                                    action = ("Press", 0);
                                }

                                if (action.Trigger != null)
                                {
                                    bool shouldClose = false;

                                    if (action.Trigger.Contains("Hold"))
                                    {
                                        shouldClose = true;
                                    }
                                    else if (action.Trigger == "LongPress")
                                    {
                                        if (vk == 0x02u)
                                        {
                                            shouldClose = true;
                                        }
                                        else if (isDown)
                                        {
                                            lastTicks[vk] = Environment.TickCount64;
                                        }
                                        else
                                        {
                                            if (lastTicks.TryGetValue(vk, out long t) && t != 0)
                                            {
                                                if ((Environment.TickCount64 - t) >= action.Threshold)
                                                {
                                                    shouldClose = true;
                                                }
                                            }
                                            lastTicks[vk] = 0;
                                        }
                                    }
                                    else if (action.Trigger == "DoubleTap")
                                    {
                                        if (vk == 0x02u)
                                        {
                                            shouldClose = true;
                                        }
                                        else if (isDown)
                                        {
                                            long currentTick = Environment.TickCount64;
                                            if (lastTicks.TryGetValue(vk, out long t) && t != 0 && (currentTick - t) < action.Threshold)
                                            {
                                                shouldClose = true;
                                                lastTicks[vk] = 0;
                                            }
                                        }
                                        else
                                        {
                                            lastTicks[vk] = Environment.TickCount64;
                                        }
                                    }
                                    else
                                    {
                                        shouldClose = (action.Trigger == "Release") ? !isDown : isDown;
                                    }

                                    if (shouldClose)
                                    {
                                        _isChat = false;
                                        ResetHangulState();
                                    }
                                }
                            }
                        }

                        if (HasStateChanged(vk, isDown))
                        {
                            OnInputEvent?.Invoke(this, new InputEventArgs(vk, isDown));
                        }
                    }
                }
                return CallNextHookEx(mouseHook, nCode, wParam, lParam);
            }

            private bool HasStateChanged(uint vkCode, bool isDown)
            {
                lock (keyStates)
                {
                    if (keyStates.TryGetValue(vkCode, out bool currentState))
                    {
                        if (currentState == isDown) return false;
                    }
                    keyStates[vkCode] = isDown;
                    return true;
                }
            }

            private bool ProcessHangulBypass(uint vkCode, bool isDown)
            {
                if (IsHangulShiftKey(vkCode))
                {
                    // Shift 자체가 게임 채팅창으로 전달되면 조합 중인 글자가 확정될 수 있어 내부 수정키로만 사용한다.
                    bool shouldKeepInsideComposer = isHangulMode && (engine.IsComposing() || lastInjected.Length > 0 || isDown);
                    if (shouldKeepInsideComposer)
                        HasStateChanged(vkCode, isDown);

                    return shouldKeepInsideComposer;
                }

                if (vkCode == (uint)Keys.ControlKey || vkCode == (uint)Keys.LControlKey || vkCode == (uint)Keys.RControlKey ||
                    vkCode == (uint)Keys.Menu || vkCode == (uint)Keys.LMenu || vkCode == (uint)Keys.RMenu || vkCode == 0x09)
                    return false;

                if (!isHangulMode)
                    return false;

                if (!isDown && injectedShiftSymbolKeys.Remove(vkCode))
                    return true;

                // 한글 조합 보호를 위해 Shift 자체는 게임에 보내지 않으므로 Shift+숫자/구두점은 기호를 직접 주입한다.
                if (isDown && IsShiftDownForHangulInput() && TryGetShiftedSymbol(vkCode, out char shiftedSymbol))
                {
                    if (engine.IsComposing())
                    {
                        engine.Flush();
                        ExecuteInjectDiff(lastInjected, engine.GetCurrentText());
                    }

                    ResetHangulState();
                    ExecuteInjectDiff("", shiftedSymbol.ToString());
                    injectedShiftSymbolKeys.Add(vkCode);
                    return true;
                }

                bool isAlphabet = (vkCode >= 0x41 && vkCode <= 0x5A);
                bool isBack = (vkCode == (uint)Keys.Back);

                if (!isAlphabet && !isBack)
                {
                    if (isDown && engine.IsComposing())
                    {
                        engine.Flush();
                        ExecuteInjectDiff(lastInjected, engine.GetCurrentText());
                        ResetHangulState();
                    }
                    return false;
                }

                if (!isDown)
                    return engine.IsComposing();

                if (isBack)
                {
                    if (!engine.IsComposing() && lastInjected.Length == 0) return false;
                    engine.Backspace();
                }
                else
                {
                    bool isShift = IsShiftDownForHangulInput();
                    if (!engine.ProcessInput(vkCode, isShift))
                    {
                        engine.Flush();
                        ResetHangulState();
                        return false;
                    }
                }

                string nextText = engine.GetCurrentText();
                ExecuteInjectDiff(lastInjected, nextText);
                lastInjected = nextText;

                return true;
            }

            private static bool IsHangulShiftKey(uint vkCode)
            {
                return vkCode == (uint)Keys.ShiftKey || vkCode == (uint)Keys.LShiftKey || vkCode == (uint)Keys.RShiftKey;
            }

            private static bool TryGetShiftedSymbol(uint vkCode, out char symbol)
            {
                // 한국어/영어 QWERTY에서 Shift로 만드는 숫자열과 구두점 키를 유니코드 문자로 변환한다.
                symbol = vkCode switch
                {
                    0x31 => '!',
                    0x32 => '@',
                    0x33 => '#',
                    0x34 => '$',
                    0x35 => '%',
                    0x36 => '^',
                    0x37 => '&',
                    0x38 => '*',
                    0x39 => '(',
                    0x30 => ')',
                    0xBD => '_',
                    0xBB => '+',
                    0xDB => '{',
                    0xDD => '}',
                    0xDC => '|',
                    0xBA => ':',
                    0xDE => '"',
                    0xBC => '<',
                    0xBE => '>',
                    0xBF => '?',
                    0xC0 => '~',
                    _ => '\0'
                };

                return symbol != '\0';
            }

            private void ExecuteInjectDiff(string prev, string curr)
            {
                if (prev == curr) return;

                int common = 0;
                int minLength = Math.Min(prev.Length, curr.Length);
                while (common < minLength && prev[common] == curr[common])
                {
                    common++;
                }

                int bsCount = prev.Length - common;

                if (bsCount > 0)
                {
                    INPUT[] bsInputs = new INPUT[bsCount * 2];
                    for (int i = 0; i < bsCount; i++)
                    {
                        bsInputs[i * 2] = CreateInput((ushort)Keys.Back, 0x0E, 0);
                        bsInputs[i * 2 + 1] = CreateInput((ushort)Keys.Back, 0x0E, 0x0002);
                    }

                    SendInput((uint)bsInputs.Length, bsInputs, Marshal.SizeOf(typeof(INPUT)));
                    Thread.Sleep(30);
                }

                if (common < curr.Length)
                {
                    string toAdd = curr.Substring(common);
                    INPUT[] inInputs = new INPUT[toAdd.Length * 2];
                    for (int i = 0; i < toAdd.Length; i++)
                    {
                        inInputs[i * 2] = CreateInput(0, (ushort)toAdd[i], 0x0004);
                        inInputs[i * 2 + 1] = CreateInput(0, (ushort)toAdd[i], 0x0004 | 0x0002);
                    }

                    SendInput((uint)inInputs.Length, inInputs, Marshal.SizeOf(typeof(INPUT)));
                    Thread.Sleep(2);
                }
            }

            private bool IsShiftDownForHangulInput()
            {
                bool trackedShift;
                lock (keyStates)
                {
                    trackedShift = (keyStates.TryGetValue(16, out bool s) && s) ||
                        (keyStates.TryGetValue(160, out bool ls) && ls) ||
                        (keyStates.TryGetValue(161, out bool rs) && rs);
                }

                // ㄲ/ㅆ 같은 받침은 Shift+R/T 순간에 결정되므로, 후킹 상태표와 실제 키 상태를 같이 확인한다.
                bool physicalShift = (GetAsyncKeyState((int)Keys.ShiftKey) & 0x8000) != 0 ||
                    (GetAsyncKeyState((int)Keys.LShiftKey) & 0x8000) != 0 ||
                    (GetAsyncKeyState((int)Keys.RShiftKey) & 0x8000) != 0;

                return trackedShift || physicalShift;
            }

            private void ResetHangulState()
            {
                engine.Clear();
                lastInjected = "";
                injectedShiftSymbolKeys.Clear();
            }

            private INPUT CreateInput(ushort vk, ushort scan, uint flags)
            {
                INPUT input = new INPUT { type = 1 };
                input.ki.wVk = vk;
                input.ki.wScan = scan;
                input.ki.dwFlags = flags;
                input.ki.time = 0;
                input.ki.dwExtraInfo = IntPtr.Zero;
                return input;
            }

            public void Dispose()
            {
                if (keyboardHook != IntPtr.Zero)
                    UnhookWindowsHookEx(keyboardHook);

                if (mouseHook != IntPtr.Zero)
                    UnhookWindowsHookEx(mouseHook);
            }

            #region WinAPI
            [DllImport("user32.dll", SetLastError = true)]
            private static extern IntPtr SetWindowsHookEx(int idHook, Delegate lpfn, IntPtr hMod, uint dwThreadId);

            [DllImport("user32.dll")]
            private static extern bool UnhookWindowsHookEx(IntPtr hhk);

            [DllImport("user32.dll")]
            private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

            [DllImport("user32.dll")]
            private static extern short GetAsyncKeyState(int vKey);

            [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            private static extern IntPtr GetModuleHandle(string? lpModuleName);

            [StructLayout(LayoutKind.Explicit, Size = 40)]
            private struct INPUT
            {
                [FieldOffset(0)] public uint type;
                [FieldOffset(8)] public KEYBDINPUT ki;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct KEYBDINPUT
            {
                public ushort wVk;
                public ushort wScan;
                public uint dwFlags;
                public uint time;
                public IntPtr dwExtraInfo;
            }
            #endregion
        }

        public static class CursorUtil
        {
            [StructLayout(LayoutKind.Sequential)]
            private struct CURSORINFO
            {
                public int cbSize;
                public int flags;
                public IntPtr hCursor;
                public Point ptScreenPos;
            }

            [DllImport("user32.dll")]
            private static extern bool GetCursorInfo(out CURSORINFO pci);

            public static bool IsVisible()
            {
                CURSORINFO ci = new CURSORINFO();
                ci.cbSize = Marshal.SizeOf(ci);

                if (GetCursorInfo(out ci))
                {
                    return (ci.flags & 0x00000001) != 0;
                }

                return false;
            }
        }

        private static class HelldiversAudioMuteController
        {
            private const string GameProcessName = "helldivers2";

            public static void MuteGameSessions(Dictionary<string, bool> rememberedMuteStates)
            {
                ForEachGameSession(session =>
                {
                    if (!rememberedMuteStates.ContainsKey(session.Id))
                        rememberedMuteStates[session.Id] = session.IsMuted;

                    if (!session.IsMuted)
                        session.SetMuted(true);
                });
            }

            public static void RestoreGameSessions(Dictionary<string, bool> rememberedMuteStates)
            {
                if (rememberedMuteStates.Count == 0) return;

                ForEachGameSession(session =>
                {
                    if (rememberedMuteStates.TryGetValue(session.Id, out bool originalMuteState))
                        session.SetMuted(originalMuteState);
                });

                rememberedMuteStates.Clear();
            }

            public static bool ForceUnmuteGameSessions()
            {
                bool foundGameSession = false;
                // 게임 재시작 직후 Windows가 이전 앱 음소거 상태를 이어받는 경우를 풀기 위한 안전장치다.
                ForEachGameSession(session =>
                {
                    foundGameSession = true;
                    if (session.IsMuted)
                        session.SetMuted(false);
                });

                return foundGameSession;
            }

            public static bool IsGameProcessRunning()
            {
                try
                {
                    using Process currentProcess = Process.GetCurrentProcess();
                    Process[] processes = Process.GetProcessesByName(GameProcessName);
                    try
                    {
                        return processes.Length > 0;
                    }
                    finally
                    {
                        foreach (var process in processes)
                            process.Dispose();
                    }
                }
                catch
                {
                    return false;
                }
            }

            private static void ForEachGameSession(Action<AudioSession> action)
            {
                IMMDeviceEnumerator? enumerator = null;
                IMMDevice? device = null;
                IAudioSessionManager2? manager = null;
                IAudioSessionEnumerator? sessions = null;

                try
                {
                    enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                    if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device) != 0 || device == null)
                        return;

                    Guid managerId = typeof(IAudioSessionManager2).GUID;
                    if (device.Activate(ref managerId, CLSCTX.CLSCTX_ALL, IntPtr.Zero, out object managerObject) != 0)
                        return;

                    manager = (IAudioSessionManager2)managerObject;
                    if (manager.GetSessionEnumerator(out sessions) != 0 || sessions == null)
                        return;

                    if (sessions.GetCount(out int count) != 0)
                        return;

                    for (int i = 0; i < count; i++)
                    {
                        IAudioSessionControl? control = null;
                        IAudioSessionControl2? control2 = null;
                        ISimpleAudioVolume? volume = null;

                        try
                        {
                            if (sessions.GetSession(i, out control) != 0 || control == null)
                                continue;

                            control2 = control as IAudioSessionControl2;
                            volume = control as ISimpleAudioVolume;
                            if (control2 == null || volume == null)
                                continue;

                            if (control2.GetProcessId(out uint processId) != 0 || !IsHelldiversProcess(processId))
                                continue;

                            string sessionId = $"pid:{processId}:index:{i}";
                            if (control2.GetSessionInstanceIdentifier(out string instanceId) == 0 && !string.IsNullOrWhiteSpace(instanceId))
                                sessionId = instanceId;

                            if (volume.GetMute(out bool isMuted) != 0)
                                continue;

                            action(new AudioSession(sessionId, isMuted, value =>
                            {
                                Guid eventContext = Guid.Empty;
                                volume.SetMute(value, ref eventContext);
                            }));
                        }
                        finally
                        {
                            // 세션 제어 인터페이스들은 같은 RCW에서 캐스팅되므로 원본 control만 해제해 과해제를 피한다.
                            ReleaseComObject(control);
                        }
                    }
                }
                catch
                {
                    // 오디오 장치 전환 중에는 CoreAudio 세션 열거가 실패할 수 있으므로 다음 타이머 틱에서 다시 시도한다.
                }
                finally
                {
                    ReleaseComObject(sessions);
                    ReleaseComObject(manager);
                    ReleaseComObject(device);
                    ReleaseComObject(enumerator);
                }
            }

            private static bool IsHelldiversProcess(uint processId)
            {
                try
                {
                    using var process = Process.GetProcessById((int)processId);
                    return process.ProcessName.Equals(GameProcessName, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }

            private static void ReleaseComObject(object? value)
            {
                if (value != null && Marshal.IsComObject(value))
                    Marshal.ReleaseComObject(value);
            }

            private sealed record AudioSession(string Id, bool IsMuted, Action<bool> SetMuted);

            private enum EDataFlow
            {
                eRender = 0
            }

            private enum ERole
            {
                eMultimedia = 1
            }

            private enum AudioSessionState
            {
                AudioSessionStateInactive = 0,
                AudioSessionStateActive = 1,
                AudioSessionStateExpired = 2
            }

            [Flags]
            private enum CLSCTX : uint
            {
                CLSCTX_INPROC_SERVER = 0x1,
                CLSCTX_INPROC_HANDLER = 0x2,
                CLSCTX_LOCAL_SERVER = 0x4,
                CLSCTX_REMOTE_SERVER = 0x10,
                CLSCTX_ALL = CLSCTX_INPROC_SERVER | CLSCTX_INPROC_HANDLER | CLSCTX_LOCAL_SERVER | CLSCTX_REMOTE_SERVER
            }

            [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
            private class MMDeviceEnumerator
            {
            }

            [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
            private interface IMMDeviceEnumerator
            {
                [PreserveSig]
                int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out object ppDevices);

                [PreserveSig]
                int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
            }

            [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
            private interface IMMDevice
            {
                [PreserveSig]
                int Activate(ref Guid iid, CLSCTX dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
            }

            [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
            private interface IAudioSessionManager2
            {
                [PreserveSig]
                int GetAudioSessionControl(IntPtr audioSessionGuid, uint streamFlags, out object sessionControl);

                [PreserveSig]
                int GetSimpleAudioVolume(IntPtr audioSessionGuid, uint streamFlags, out object audioVolume);

                [PreserveSig]
                int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
            }

            [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
            private interface IAudioSessionEnumerator
            {
                [PreserveSig]
                int GetCount(out int sessionCount);

                [PreserveSig]
                int GetSession(int sessionCount, out IAudioSessionControl session);
            }

            [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
            private interface IAudioSessionControl
            {
                [PreserveSig]
                int GetState(out AudioSessionState state);

                [PreserveSig]
                int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

                [PreserveSig]
                int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);

                [PreserveSig]
                int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

                [PreserveSig]
                int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);

                [PreserveSig]
                int GetGroupingParam(out Guid groupingId);

                [PreserveSig]
                int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

                [PreserveSig]
                int RegisterAudioSessionNotification(IntPtr newNotifications);

                [PreserveSig]
                int UnregisterAudioSessionNotification(IntPtr newNotifications);
            }

            [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
            private interface IAudioSessionControl2
            {
                [PreserveSig]
                int GetState(out AudioSessionState state);

                [PreserveSig]
                int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

                [PreserveSig]
                int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);

                [PreserveSig]
                int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

                [PreserveSig]
                int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);

                [PreserveSig]
                int GetGroupingParam(out Guid groupingId);

                [PreserveSig]
                int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

                [PreserveSig]
                int RegisterAudioSessionNotification(IntPtr newNotifications);

                [PreserveSig]
                int UnregisterAudioSessionNotification(IntPtr newNotifications);

                [PreserveSig]
                int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);

                [PreserveSig]
                int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);

                [PreserveSig]
                int GetProcessId(out uint retVal);

                [PreserveSig]
                int IsSystemSoundsSession();

                [PreserveSig]
                int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
            }

            [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
            private interface ISimpleAudioVolume
            {
                [PreserveSig]
                int SetMasterVolume(float level, ref Guid eventContext);

                [PreserveSig]
                int GetMasterVolume(out float level);

                [PreserveSig]
                int SetMute([MarshalAs(UnmanagedType.Bool)] bool isMuted, ref Guid eventContext);

                [PreserveSig]
                int GetMute([MarshalAs(UnmanagedType.Bool)] out bool isMuted);
            }
        }

        public static class Logger
        {
            private static readonly string logFile =
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt");

            private static readonly object locker = new();

            public static void Log(string msg)
            {
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";

                lock (locker)
                {
                    File.AppendAllText(logFile, line + Environment.NewLine);
                }
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // 훅을 해제하기 전에 프로그램이 주입해 둔 보기 키부터 놓아 종료 후 modifier가 남지 않게 한다.
            ReleaseOverlayStratagemViewKey("프로그램 종료");
            _inputHook?.Dispose();
            _crosshairTimer?.Stop();
            _crosshairTimer?.Dispose();
            _supportWeaponGaugeTimer?.Stop();
            _supportWeaponGaugeTimer?.Dispose();
            _autoReloadDetectionTimer?.Stop();
            _autoReloadDetectionTimer?.Dispose();
            _inactiveGameAudioMuteTimer?.Stop();
            _inactiveGameAudioMuteTimer?.Dispose();
            _softwareCursorTimer?.Stop();
            _softwareCursorTimer?.Dispose();
            HelldiversAudioMuteController.RestoreGameSessions(_gameAudioMuteStatesBeforeHelper);
            _padLoopCts?.Cancel();
            _padLoopCts?.Dispose();
            _ocrRegionSettingsForm?.Dispose();
            _autoReloadCalibrationForm?.Dispose();
            _crosshairEditorForm?.Dispose();
            _crosshairForm?.Dispose();
            _supportWeaponGaugeForm?.Dispose();
            _ocrDebugOverlayForm?.Dispose();
            _ocrRegionOverlayForm?.Dispose();
            _stratagemSelectionDebugForm?.Dispose();
            _softwareCursorOverlayForm?.Dispose();

            _overlayForm?.Dispose();
            _presetOverlayForm?.Dispose();
            _helperEditorWindow?.Dispose();
            _webView?.Dispose();
            _webViews.Clear();

            GamepadReader.Quit();

            _webView = null;
            _overlayForm = null;
            _presetOverlayForm = null;
            _helperEditorWindow = null;
            _ocrRegionSettingsForm = null;
            _autoReloadCalibrationForm = null;
            _crosshairEditorForm = null;
            _crosshairForm = null;
            _supportWeaponGaugeForm = null;
            _ocrDebugOverlayForm = null;
            _ocrRegionOverlayForm = null;
            _softwareCursorOverlayForm = null;
            _stratagemSelectionDebugForm = null;

            _crosshairTimer = null;
            _supportWeaponGaugeTimer = null;
            _autoReloadDetectionTimer = null;
            _inactiveGameAudioMuteTimer = null;
            _padLoopCts = null;
            _inputHook = null;

            _parsedData.Clear();
            _sequenceMap.Clear();

            foreach (var img in _imageCache.Values) img?.Dispose();
            _imageCache.Clear();

            base.OnFormClosed(e);
            Environment.Exit(0);
        }
    }
}
