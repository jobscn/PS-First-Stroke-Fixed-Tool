using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

public class MemoryModifier
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, uint nSize, out int lpNumberOfBytesRead);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out int lpNumberOfBytesWritten);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    const uint PROCESS_VM_WRITE = 0x0020;
    const uint PROCESS_VM_OPERATION = 0x0008;
    const uint PROCESS_QUERY_INFORMATION = 0x0400;
    const uint PROCESS_VM_READ = 0x0010;

    private static string processName = "Photoshop";
    private static long offset = 0x9D36768; // 请确保针对您的PS版本是正确的
    // private static long valueToWrite = 140698365506448; // This will be captured dynamically
    private static long valueToWrite; // Will store the captured value
    private static bool valueToLockCaptured = false; // Flag to indicate if the value has been captured

    private static IntPtr processHandle = IntPtr.Zero;
    private static IntPtr baseAddress = IntPtr.Zero;
    private static Process targetProcess = null;
    private static volatile bool keepRunning = true;

    private static bool isCurrentlyConnected = false;
    private static bool hasPrintedFailureMessageInThisDisconnectCycle = false;
    private static int lastPrintedWriteErrorCode = 0; // Also used for capture phase status codes

    // DEBUG模式专用变量
    private static int lastDebugReadErrorCode = 0;
    private static long lastDisplayedDebugValue;
    private static bool hasDisplayedInitialDebugValue = false;

    private static int lockIntervalMilliseconds = 10;
    private static int debugReadIntervalMilliseconds = 1000;
    private static bool debugMode = false;
    private static Thread debugReadThread = null;

    private static bool AcquireTargetProcess()
    {
        if (processHandle != IntPtr.Zero) { CloseHandle(processHandle); processHandle = IntPtr.Zero; }
        baseAddress = IntPtr.Zero;
        Process localTargetProcess = null;

        Process[] processes;
        try { processes = Process.GetProcessesByName(processName); }
        catch (Exception ex)
        {
            if (!hasPrintedFailureMessageInThisDisconnectCycle)
            {
                Console.WriteLine($"{DateTime.Now}: 查找进程 {processName} 时发生错误: {ex.Message}");
                hasPrintedFailureMessageInThisDisconnectCycle = true;
            }
            isCurrentlyConnected = false; targetProcess = null; return false;
        }

        if (processes.Length == 0)
        {
            if (!hasPrintedFailureMessageInThisDisconnectCycle)
            {
                Console.WriteLine($"{DateTime.Now}: {processName}.exe 未找到. 等待其启动...");
                hasPrintedFailureMessageInThisDisconnectCycle = true;
            }
            isCurrentlyConnected = false; targetProcess = null; return false;
        }

        localTargetProcess = processes[0];
        processHandle = OpenProcess(PROCESS_VM_WRITE | PROCESS_VM_OPERATION | PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, localTargetProcess.Id);

        if (processHandle == IntPtr.Zero)
        {
            if (!hasPrintedFailureMessageInThisDisconnectCycle)
            {
                Console.WriteLine($"{DateTime.Now}: 无法打开进程 {localTargetProcess.ProcessName} (ID: {localTargetProcess.Id}). 错误: {Marshal.GetLastWin32Error()} ({GetErrorMessage(Marshal.GetLastWin32Error())}).");
                hasPrintedFailureMessageInThisDisconnectCycle = true;
            }
            isCurrentlyConnected = false; targetProcess = null; return false;
        }

        try
        {
            if (localTargetProcess.HasExited)
            {
                if (!hasPrintedFailureMessageInThisDisconnectCycle)
                {
                    Console.WriteLine($"{DateTime.Now}: 进程 {localTargetProcess.ProcessName} (ID: {localTargetProcess.Id}) 在获取模块前已退出。");
                    hasPrintedFailureMessageInThisDisconnectCycle = true;
                }
                CloseHandle(processHandle); processHandle = IntPtr.Zero; isCurrentlyConnected = false; targetProcess = null; return false;
            }

            foreach (ProcessModule module in localTargetProcess.Modules)
            {
                if (!localTargetProcess.HasExited && module.ModuleName.Equals(processName + ".exe", StringComparison.OrdinalIgnoreCase))
                {
                    baseAddress = module.BaseAddress; break;
                }
            }
        }
        catch (Exception ex)
        {
            if (!hasPrintedFailureMessageInThisDisconnectCycle)
            {
                Console.WriteLine($"{DateTime.Now}: 访问进程 {localTargetProcess.ProcessName} (ID: {localTargetProcess.Id}) 模块时出错: {ex.Message}");
                hasPrintedFailureMessageInThisDisconnectCycle = true;
            }
            CloseHandle(processHandle); processHandle = IntPtr.Zero; isCurrentlyConnected = false; targetProcess = null; return false;
        }

        if (baseAddress == IntPtr.Zero)
        {
            if (!hasPrintedFailureMessageInThisDisconnectCycle)
            {
                Console.WriteLine($"{DateTime.Now}: 未找到 {processName}.exe 的模块基地址 (进程 ID: {localTargetProcess.Id}).");
                hasPrintedFailureMessageInThisDisconnectCycle = true;
            }
            CloseHandle(processHandle); processHandle = IntPtr.Zero; isCurrentlyConnected = false; targetProcess = null; return false;
        }

        if (!isCurrentlyConnected) // Only print this if it's a new connection
        {
            Console.WriteLine($"{DateTime.Now}: 成功连接到 {localTargetProcess.ProcessName} (ID: {localTargetProcess.Id}, 基地址: 0x{baseAddress.ToInt64():X})。服务准备就绪...");
        }
        isCurrentlyConnected = true;
        hasPrintedFailureMessageInThisDisconnectCycle = false;
        lastPrintedWriteErrorCode = 0;
        // 重置DEBUG相关状态
        lastDebugReadErrorCode = 0;
        hasDisplayedInitialDebugValue = false;
        valueToLockCaptured = false; // IMPORTANT: Reset capture flag on new/re-acquired process
        targetProcess = localTargetProcess;
        return true;
    }

    private static void DebugReadLoop()
    {
        Console.WriteLine($"{DateTime.Now}: DEBUG - 读取线程已启动，间隔: {debugReadIntervalMilliseconds}ms. 将仅在值变化时显示。");
        byte[] readValueBuffer = new byte[8]; // long is 8 bytes

        while (keepRunning && debugMode)
        {
            if (isCurrentlyConnected && processHandle != IntPtr.Zero && baseAddress != IntPtr.Zero)
            {
                IntPtr targetAddress;
                try
                {
                    if (IntPtr.Size == 8) { targetAddress = new IntPtr(baseAddress.ToInt64() + offset); }
                    else
                    {
                        if (offset > int.MaxValue || offset < int.MinValue)
                        {
                            if (lastDebugReadErrorCode != -300)
                            {
                                Console.WriteLine($"{DateTime.Now}: DEBUG_READ - 偏移量 0x{offset:X} 对于32位寻址过大。");
                                lastDebugReadErrorCode = -300;
                            }
                            Thread.Sleep(debugReadIntervalMilliseconds); continue;
                        }
                        targetAddress = IntPtr.Add(baseAddress, (int)offset);
                    }
                }
                catch (Exception ex)
                {
                    if (lastDebugReadErrorCode != -301)
                    {
                        Console.WriteLine($"{DateTime.Now}: DEBUG_READ - 计算目标地址时出错: {ex.Message}");
                        lastDebugReadErrorCode = -301;
                    }
                    Thread.Sleep(debugReadIntervalMilliseconds); continue;
                }

                int bytesReadDebug;
                bool readSuccess = ReadProcessMemory(processHandle, targetAddress, readValueBuffer, (uint)readValueBuffer.Length, out bytesReadDebug);

                if (readSuccess && bytesReadDebug == readValueBuffer.Length)
                {
                    if (!BitConverter.IsLittleEndian) { Array.Reverse(readValueBuffer); }
                    long currentValueInDebug = BitConverter.ToInt64(readValueBuffer, 0);

                    if (!hasDisplayedInitialDebugValue || currentValueInDebug != lastDisplayedDebugValue)
                    {
                        Console.WriteLine($"{DateTime.Now}: DEBUG_READ - Addr: 0x{targetAddress.ToInt64():X}, Current Value: {currentValueInDebug} (0x{currentValueInDebug:X})");
                        lastDisplayedDebugValue = currentValueInDebug;
                        hasDisplayedInitialDebugValue = true;
                    }
                    if (lastDebugReadErrorCode != 0) lastDebugReadErrorCode = 0;
                }
                else
                {
                    int debugReadErr = readSuccess ? -100 : Marshal.GetLastWin32Error();
                    if (debugReadErr != lastDebugReadErrorCode)
                    {
                        if (readSuccess) Console.WriteLine($"{DateTime.Now}: DEBUG_READ - 读取内存不足8字节 (实际: {bytesReadDebug}) at 0x{targetAddress.ToInt64():X}");
                        else Console.WriteLine($"{DateTime.Now}: DEBUG_READ - 读取当前值失败 from 0x{targetAddress.ToInt64():X}. Error: {debugReadErr} ({GetErrorMessage(debugReadErr)})");
                        lastDebugReadErrorCode = debugReadErr;
                    }
                }
            }
            Thread.Sleep(debugReadIntervalMilliseconds);
        }
        Console.WriteLine($"{DateTime.Now}: DEBUG - 读取线程已退出。");
    }

    public static void Main(string[] args)
    {
        Console.WriteLine("--- Photoshop 内存修复工具 ---");
        Console.Write($"请输入[写入]锁间隔的毫秒数 (默认: {lockIntervalMilliseconds}): ");
        string intervalInput = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(intervalInput) && int.TryParse(intervalInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedInterval) && parsedInterval > 0)
            lockIntervalMilliseconds = parsedInterval;
        else if (!string.IsNullOrWhiteSpace(intervalInput)) Console.WriteLine("无效的毫秒数输入，将使用默认值。");
        Console.WriteLine($"[写入]锁间隔设置为: {lockIntervalMilliseconds} 毫秒");

        Console.Write("是否启用DEBUG模式 (仅读取并显示变化的值)? (Y/N, 默认: N): ");
        string debugInput = Console.ReadLine();
        if (debugInput.Trim().Equals("Y", StringComparison.OrdinalIgnoreCase))
        {
            debugMode = true;
            Console.WriteLine("DEBUG模式已启用。");
            Console.Write($"请输入DEBUG[读取]间隔的毫秒数 (默认: {debugReadIntervalMilliseconds}): ");
            string debugIntervalInput = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(debugIntervalInput) && int.TryParse(debugIntervalInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedDebugInterval) && parsedDebugInterval > 0)
                debugReadIntervalMilliseconds = parsedDebugInterval;
            else if (!string.IsNullOrWhiteSpace(debugIntervalInput)) Console.WriteLine("无效的DEBUG读取毫秒数输入，将使用默认值。");
            Console.WriteLine($"DEBUG[读取]间隔设置为: {debugReadIntervalMilliseconds} 毫秒");
        }
        else { debugMode = false; Console.WriteLine("DEBUG模式已禁用。"); }

        if (debugMode) {
            Console.WriteLine($"程序启动 - 仅监控模式: 读取 {processName}.exe + 0x{offset:X}");
        } else {
            Console.WriteLine($"程序启动 - 将首先读取 {processName}.exe + 0x{offset:X} 的一个非零值，然后将其锁定。");
        }
        Console.WriteLine("将持续尝试，按 Ctrl+C 退出程序。");
        Console.WriteLine("--------------------------------------------------");

        Console.CancelKeyPress += new ConsoleCancelEventHandler(OnExit);
        byte[] bufferToWrite = null; // Will be set after value is captured

        while (keepRunning)
        {
            bool needsReacquire = false;
            if (!isCurrentlyConnected) { needsReacquire = true; }
            else
            {
                try { if (targetProcess == null || targetProcess.HasExited || processHandle == IntPtr.Zero || baseAddress == IntPtr.Zero) needsReacquire = true; }
                catch (Exception) { needsReacquire = true; }
            }

            if (needsReacquire)
            {
                if (isCurrentlyConnected) // Connection was lost
                {
                    Console.WriteLine($"{DateTime.Now}: {processName}.exe 连接丢失或已退出。{(debugMode ? "停止监控。" : "正在尝试重新连接并重新捕获锁定值...")}");
                    isCurrentlyConnected = false;
                    hasPrintedFailureMessageInThisDisconnectCycle = false; // Allow new failure messages
                    lastPrintedWriteErrorCode = 0;
                    valueToLockCaptured = false; // Reset capture flag
                    hasDisplayedInitialDebugValue = false; // Reset for debug mode
                    if (processHandle != IntPtr.Zero) { CloseHandle(processHandle); processHandle = IntPtr.Zero; }
                    baseAddress = IntPtr.Zero; targetProcess = null;
                }

                if (!AcquireTargetProcess())
                {
                    // Wait before retrying to acquire
                    Thread.Sleep(Math.Max(1000, lockIntervalMilliseconds)); // Ensure at least 1 sec wait if lockInterval is too short
                    continue;
                }
                else // Successfully reacquired
                {
                    // valueToLockCaptured is already reset in AcquireTargetProcess
                    if (debugMode && (debugReadThread == null || !debugReadThread.IsAlive))
                    {
                        debugReadThread = new Thread(DebugReadLoop) { IsBackground = true };
                        debugReadThread.Start();
                    }
                }
            }

            if (!isCurrentlyConnected || !keepRunning) { Thread.Sleep(lockIntervalMilliseconds); continue; }

            if (debugMode)
            {
                Thread.Sleep(lockIntervalMilliseconds); // Main thread sleeps while debug thread works
                continue;
            }

            // --- Non-DEBUG mode: Capture value if not yet captured, then write ---
            if (!valueToLockCaptured)
            {
                IntPtr addressToReadFrom;
                try
                {
                    if (IntPtr.Size == 8) { addressToReadFrom = new IntPtr(baseAddress.ToInt64() + offset); }
                    else
                    {
                        if (offset > int.MaxValue || offset < int.MinValue)
                        {
                            if (lastPrintedWriteErrorCode != -3)
                            {
                                Console.WriteLine($"{DateTime.Now}: CAPTURE_READ - 偏移量 0x{offset:X} 对于32位寻址过大。");
                                lastPrintedWriteErrorCode = -3;
                            }
                            Thread.Sleep(lockIntervalMilliseconds); continue;
                        }
                        addressToReadFrom = IntPtr.Add(baseAddress, (int)offset);
                    }

                    byte[] readBuffer = new byte[8]; // For long
                    int bytesRead;
                    bool readSuccess = ReadProcessMemory(processHandle, addressToReadFrom, readBuffer, (uint)readBuffer.Length, out bytesRead);

                    if (readSuccess && bytesRead == readBuffer.Length)
                    {
                        if (!BitConverter.IsLittleEndian) { Array.Reverse(readBuffer); }
                        long initialValue = BitConverter.ToInt64(readBuffer, 0);

                        if (initialValue != 0)
                        {
                            valueToWrite = initialValue;
                            bufferToWrite = BitConverter.GetBytes(valueToWrite);
                            if (!BitConverter.IsLittleEndian) { Array.Reverse(bufferToWrite); }
                            valueToLockCaptured = true;
                            Console.WriteLine($"{DateTime.Now}: SUCCESS_CAPTURE - 已捕获锁定值: {valueToWrite} (0x{valueToWrite:X}) 从地址 0x{addressToReadFrom.ToInt64():X}.");
                            Console.WriteLine($"{DateTime.Now}: 将以此值进行锁定，间隔: {lockIntervalMilliseconds}ms.");
                            lastPrintedWriteErrorCode = 0; // Clear previous status
                        }
                        else
                        {
                            if (lastPrintedWriteErrorCode != -4)
                            {
                                Console.WriteLine($"{DateTime.Now}: WAITING_CAPTURE - 从 0x{addressToReadFrom.ToInt64():X} 读取到零值。等待非零值...");
                                lastPrintedWriteErrorCode = -4;
                            }
                            Thread.Sleep(lockIntervalMilliseconds); continue;
                        }
                    }
                    else
                    {
                        int captureReadErr = readSuccess ? -101 : Marshal.GetLastWin32Error();
                        if (captureReadErr != lastPrintedWriteErrorCode)
                        {
                            if (readSuccess) Console.WriteLine($"{DateTime.Now}: CAPTURE_READ - 从 0x{addressToReadFrom.ToInt64():X} 读取的字节数不足 (实际: {bytesRead}).");
                            else Console.WriteLine($"{DateTime.Now}: CAPTURE_READ - 从 0x{addressToReadFrom.ToInt64():X} 读取初始值失败. Error: {captureReadErr} ({GetErrorMessage(captureReadErr)})");
                            lastPrintedWriteErrorCode = captureReadErr;
                        }
                        Thread.Sleep(lockIntervalMilliseconds); continue;
                    }
                }
                catch (Exception ex)
                {
                    if (lastPrintedWriteErrorCode != -5)
                    {
                        Console.WriteLine($"{DateTime.Now}: CAPTURE_READ - 读取初始值时发生异常: {ex.Message}");
                        lastPrintedWriteErrorCode = -5;
                    }
                    isCurrentlyConnected = false; // Force re-acquire in next loop
                    Thread.Sleep(lockIntervalMilliseconds); continue;
                }
            }

            if (!valueToLockCaptured) // If still not captured (e.g. value was 0, or read failed)
            {
                Thread.Sleep(lockIntervalMilliseconds);
                continue;
            }

            // --- Actual Write Logic (only if not in debug mode and value captured) ---
            try
            {
                IntPtr targetAddress;
                if (IntPtr.Size == 8) { targetAddress = new IntPtr(baseAddress.ToInt64() + offset); }
                else
                {
                    if (offset > int.MaxValue || offset < int.MinValue)
                    {
                        if (lastPrintedWriteErrorCode != -1) { Console.WriteLine($"{DateTime.Now}: WRITE - 偏移量 0x{offset:X} 对于32位寻址过大。"); lastPrintedWriteErrorCode = -1; }
                        Thread.Sleep(lockIntervalMilliseconds); continue;
                    }
                    targetAddress = IntPtr.Add(baseAddress, (int)offset);
                }

                int bytesWritten;
                bool successWrite = WriteProcessMemory(processHandle, targetAddress, bufferToWrite, (uint)bufferToWrite.Length, out bytesWritten);
                if (!successWrite)
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    if (errorCode != lastPrintedWriteErrorCode)
                    {
                        Console.WriteLine($"{DateTime.Now}: WRITE - 写入内存失败. Addr: 0x{targetAddress.ToInt64():X}, Value: {valueToWrite} (0x{valueToWrite:X}), Err: {errorCode} ({GetErrorMessage(errorCode)})");
                        lastPrintedWriteErrorCode = errorCode;
                    }
                    if (errorCode == 5 || errorCode == 299 || errorCode == 998 || errorCode == 6) // Critical errors suggesting handle invalidation
                    {
                        isCurrentlyConnected = false; // Force re-acquire
                    }
                }
                else // Write was successful
                {
                    if (lastPrintedWriteErrorCode != 0 && lastPrintedWriteErrorCode != -4) // Clear error if it wasn't the "waiting for zero" status
                    {
                        // Console.WriteLine($"{DateTime.Now}: WRITE_SUCCESS - Addr: 0x{targetAddress.ToInt64():X} locked to {valueToWrite}"); // Optional: can be very verbose
                        lastPrintedWriteErrorCode = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                if (lastPrintedWriteErrorCode != -2) { Console.WriteLine($"{DateTime.Now}: WRITE - 主循环写入操作中发生异常: {ex.Message}"); lastPrintedWriteErrorCode = -2; }
                isCurrentlyConnected = false; // Force re-acquire
            }
            Thread.Sleep(lockIntervalMilliseconds);
        }
        Console.WriteLine("程序正在退出..."); Cleanup(); Console.WriteLine("程序已退出。");
    }

    protected static void OnExit(object sender, ConsoleCancelEventArgs args)
    {
        Console.WriteLine("接收到 Ctrl+C 信号，准备退出..."); args.Cancel = true; keepRunning = false;
    }

    private static void Cleanup()
    {
        if (processHandle != IntPtr.Zero) { Console.WriteLine($"{DateTime.Now}: 关闭进程句柄 0x{processHandle.ToInt64():X}..."); CloseHandle(processHandle); processHandle = IntPtr.Zero; }
        isCurrentlyConnected = false;
    }

    private static string GetErrorMessage(int errorCode)
    {
        // Debug Read specific codes
        if (errorCode == -301) return "Error calculating target address in debug read";
        if (errorCode == -300) return "Offset too large for 32-bit address space in debug read";
        if (errorCode == -100) return "Partial read (Debug)";

        // Capture Read specific codes/statuses
        if (errorCode == -101) return "Partial read (Capture)";
        if (errorCode == -5) return "Exception during initial value read (Capture)";
        if (errorCode == -4) return "Read zero value, waiting for non-zero (Capture status)";
        if (errorCode == -3) return "Offset too large for 32-bit address space (Capture)";
        
        // General Write operation codes (can be reused if context is clear)
        // -1: Offset too large (Write)
        // -2: Exception in write loop (Write)

        // Default to Win32Exception message for other codes
        return new System.ComponentModel.Win32Exception(errorCode).Message;
    }
}