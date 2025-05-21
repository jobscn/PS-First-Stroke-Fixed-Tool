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
    private static long valueToWrite = 140699315057552; // 请确保这个值是您期望写入的

    private static IntPtr processHandle = IntPtr.Zero;
    private static IntPtr baseAddress = IntPtr.Zero;
    private static Process targetProcess = null;
    private static volatile bool keepRunning = true;

    private static bool isCurrentlyConnected = false;
    private static bool hasPrintedFailureMessageInThisDisconnectCycle = false;
    private static int lastPrintedWriteErrorCode = 0; // Used by both write strategies for error logging

    private static int lastDebugReadErrorCode = 0;
    private static long lastDisplayedDebugValue;
    private static bool hasDisplayedInitialDebugValue = false;

    private static int lockIntervalMilliseconds = 100;
    private static int debugReadIntervalMilliseconds = 1000;
    private static bool debugMode = false;
    private static Thread debugReadThread = null;
    private static bool useStrategyB = false; // New: Flag for Strategy B

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

        if (!isCurrentlyConnected)
        {
            Console.WriteLine($"{DateTime.Now}: 成功锁定 {localTargetProcess.ProcessName} (ID: {localTargetProcess.Id}, 基地址: 0x{baseAddress.ToInt64():X})。服务运行中...");
        }
        isCurrentlyConnected = true;
        hasPrintedFailureMessageInThisDisconnectCycle = false;
        lastPrintedWriteErrorCode = 0;
        lastDebugReadErrorCode = 0;
        hasDisplayedInitialDebugValue = false;
        targetProcess = localTargetProcess;
        return true;
    }

    private static void DebugReadLoop()
    {
        // ... (DebugReadLoop remains the same as your last version)
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

                    // 只有当值变化或首次显示时才打印
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
                    // 如果读取失败，我们可能希望重置 hasDisplayedInitialDebugValue，以便下次成功读取时能打印
                    // 但如果错误是持久的，这会导致每次错误恢复后都打印。暂时保持现状，只在成功连接时重置。
                }
            }
            Thread.Sleep(debugReadIntervalMilliseconds);
        }
        Console.WriteLine($"{DateTime.Now}: DEBUG - 读取线程已退出。");
    }

    public static void Main(string[] args)
    {
        // Parse command-line arguments for strategy B
        if (args != null)
        {
            foreach (string arg in args)
            {
                if (arg.Equals("/strategyB", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("-b", StringComparison.OrdinalIgnoreCase))
                {
                    useStrategyB = true;
                    break;
                }
            }
        }

        Console.WriteLine("--- Photoshop 内存修复工具 ---");
        if(useStrategyB) Console.WriteLine("*** 策略B (按需写入) 已激活 ***");

        Console.Write($"请输入[操作]间隔的毫秒数 (默认: {lockIntervalMilliseconds}): ");
        string intervalInput = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(intervalInput) && int.TryParse(intervalInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedInterval) && parsedInterval > 0)
            lockIntervalMilliseconds = parsedInterval;
        else if (!string.IsNullOrWhiteSpace(intervalInput)) Console.WriteLine("无效的毫秒数输入，将使用默认值。");
        Console.WriteLine($"[操作]间隔设置为: {lockIntervalMilliseconds} 毫秒 (用于持续写入或按需读写检查)");

        Console.Write("是否启用纯DEBUG监控模式 (仅读取并显示变化的值, 不写入)? (Y/N, 默认: N): ");
        string debugInput = Console.ReadLine();
        if (debugInput.Trim().Equals("Y", StringComparison.OrdinalIgnoreCase))
        {
            debugMode = true; // Pure debug mode overrides strategy B for writing
            useStrategyB = false; // Ensure strategy B write logic is disabled
            Console.WriteLine("纯DEBUG监控模式已启用。");
            Console.Write($"请输入DEBUG[读取]间隔的毫秒数 (默认: {debugReadIntervalMilliseconds}): ");
            string debugIntervalInput = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(debugIntervalInput) && int.TryParse(debugIntervalInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedDebugInterval) && parsedDebugInterval > 0)
                debugReadIntervalMilliseconds = parsedDebugInterval;
            else if (!string.IsNullOrWhiteSpace(debugIntervalInput)) Console.WriteLine("无效的DEBUG读取毫秒数输入，将使用默认值。");
            Console.WriteLine($"DEBUG[读取]间隔设置为: {debugReadIntervalMilliseconds} 毫秒");
        }
        else { debugMode = false; Console.WriteLine("纯DEBUG监控模式已禁用。"); }

        string modeDescription = debugMode ? "纯监控模式" :
                                 useStrategyB ? $"按需写入模式 (策略B), 目标值: {valueToWrite} (0x{valueToWrite:X})" :
                                                $"持续写入模式 (策略A), 目标值: {valueToWrite} (0x{valueToWrite:X})";
        Console.WriteLine($"程序启动 - {modeDescription}");
        Console.WriteLine("将持续尝试，按 Ctrl+C 退出程序。");
        Console.WriteLine("--------------------------------------------------");

        Console.CancelKeyPress += new ConsoleCancelEventHandler(OnExit);
        byte[] bufferToWriteBytes = BitConverter.GetBytes(valueToWrite);
        if (!BitConverter.IsLittleEndian) { Array.Reverse(bufferToWriteBytes); }

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
                if (isCurrentlyConnected)
                {
                    Console.WriteLine($"{DateTime.Now}: {processName}.exe 连接丢失或已退出，{(debugMode ? "停止监控。" : (useStrategyB ? "停止按需写入尝试..." : "正在尝试重新锁定..."))}");
                    isCurrentlyConnected = false;
                    hasPrintedFailureMessageInThisDisconnectCycle = false;
                    lastPrintedWriteErrorCode = 0;
                    hasDisplayedInitialDebugValue = false;
                    if (processHandle != IntPtr.Zero) { CloseHandle(processHandle); processHandle = IntPtr.Zero; }
                    baseAddress = IntPtr.Zero; targetProcess = null;
                }

                if (!AcquireTargetProcess())
                {
                    int sleepTimeTotal = 0;
                    while (sleepTimeTotal < lockIntervalMilliseconds && keepRunning)
                    {
                        int currentSleep = Math.Min(100, lockIntervalMilliseconds - sleepTimeTotal);
                        Thread.Sleep(currentSleep);
                        sleepTimeTotal += currentSleep;
                    }
                    continue;
                }
                else // Successfully (re)acquired
                {
                    if (debugMode && (debugReadThread == null || !debugReadThread.IsAlive))
                    {
                        debugReadThread = new Thread(DebugReadLoop) { IsBackground = true };
                        debugReadThread.Start();
                    }
                }
            }

            if (!isCurrentlyConnected || !keepRunning) { Thread.Sleep(lockIntervalMilliseconds); continue; }

            // Pure DEBUG Mode: Only reads via DebugReadLoop, main loop just keeps connection alive.
            if (debugMode)
            {
                Thread.Sleep(lockIntervalMilliseconds);
                continue;
            }

            // --- Operation Mode (Strategy A or B) ---
            try
            {
                IntPtr targetAddress;
                if (IntPtr.Size == 8) { targetAddress = new IntPtr(baseAddress.ToInt64() + offset); }
                else
                {
                    if (offset > int.MaxValue || offset < int.MinValue)
                    {
                        if (lastPrintedWriteErrorCode != -1) { Console.WriteLine($"{DateTime.Now}: 偏移量 0x{offset:X} 对于32位寻址过大。"); lastPrintedWriteErrorCode = -1; }
                        Thread.Sleep(lockIntervalMilliseconds); continue;
                    }
                    targetAddress = IntPtr.Add(baseAddress, (int)offset);
                }

                if (useStrategyB) // Strategy B: Read, then write if different
                {
                    byte[] readValueBuffer = new byte[8];
                    int bytesRead;
                    bool readSuccess = ReadProcessMemory(processHandle, targetAddress, readValueBuffer, (uint)readValueBuffer.Length, out bytesRead);

                    if (readSuccess && bytesRead == readValueBuffer.Length)
                    {
                        if (!BitConverter.IsLittleEndian) { Array.Reverse(readValueBuffer); }
                        long currentValue = BitConverter.ToInt64(readValueBuffer, 0);

                        if (currentValue != valueToWrite)
                        {
                            Console.WriteLine($"{DateTime.Now}: STRATEGY_B - 值不匹配. 读取: {currentValue} (0x{currentValue:X}), 期望: {valueToWrite}. 执行写入...");
                            int bytesWritten;
                            bool successWrite = WriteProcessMemory(processHandle, targetAddress, bufferToWriteBytes, (uint)bufferToWriteBytes.Length, out bytesWritten);
                            if (!successWrite)
                            {
                                int errorCode = Marshal.GetLastWin32Error();
                                // Use lastPrintedWriteErrorCode to avoid spamming for persistent write errors even in Strategy B
                                if (errorCode != lastPrintedWriteErrorCode)
                                {
                                   Console.WriteLine($"{DateTime.Now}: STRATEGY_B - 写入失败. Addr: 0x{targetAddress.ToInt64():X}, Err: {errorCode} ({GetErrorMessage(errorCode)})");
                                   lastPrintedWriteErrorCode = errorCode;
                                }
                                if (errorCode == 5 || errorCode == 299 || errorCode == 998 || errorCode == 6) // Critical errors
                                {
                                     if (isCurrentlyConnected) { isCurrentlyConnected = false; hasPrintedFailureMessageInThisDisconnectCycle = false; }
                                     if (processHandle != IntPtr.Zero) { CloseHandle(processHandle); processHandle = IntPtr.Zero; }
                                     baseAddress = IntPtr.Zero; targetProcess = null;
                                }
                            }
                            else // Write successful
                            {
                                if(lastPrintedWriteErrorCode != 0) lastPrintedWriteErrorCode = 0; // Reset if previous write failed
                            }
                        }
                        // else: value matches, do nothing.
                    }
                    else // Read failed for Strategy B
                    {
                        int readErrCode = Marshal.GetLastWin32Error();
                        // Avoid spamming read errors in strategy B too. Could use a dedicated error code variable or reuse write one if context is clear.
                        // For now, print if different from general lastPrintedWriteErrorCode.
                        if (readErrCode != lastPrintedWriteErrorCode && readErrCode != 0)
                        {
                            Console.WriteLine($"{DateTime.Now}: STRATEGY_B - 读取内存失败，无法执行按需写入. Error: {readErrCode} ({GetErrorMessage(readErrCode)})");
                            lastPrintedWriteErrorCode = readErrCode; // Mark this read error
                        }
                    }
                }
                else // Strategy A: Continuous Write (default if not debugMode and not useStrategyB)
                {
                    int bytesWritten;
                    bool successWrite = WriteProcessMemory(processHandle, targetAddress, bufferToWriteBytes, (uint)bufferToWriteBytes.Length, out bytesWritten);
                    if (!successWrite)
                    {
                        int errorCode = Marshal.GetLastWin32Error();
                        if (errorCode != lastPrintedWriteErrorCode)
                        {
                            Console.WriteLine($"{DateTime.Now}: STRATEGY_A - 写入内存失败. Addr: 0x{targetAddress.ToInt64():X}, Err: {errorCode} ({GetErrorMessage(errorCode)})");
                            lastPrintedWriteErrorCode = errorCode;
                        }
                        if (errorCode == 5 || errorCode == 299 || errorCode == 998 || errorCode == 6)
                        {
                            if (isCurrentlyConnected) { isCurrentlyConnected = false; hasPrintedFailureMessageInThisDisconnectCycle = false; }
                            if (processHandle != IntPtr.Zero) { CloseHandle(processHandle); processHandle = IntPtr.Zero; }
                            baseAddress = IntPtr.Zero; targetProcess = null;
                        }
                    }
                    else { if (lastPrintedWriteErrorCode != 0) lastPrintedWriteErrorCode = 0; }
                }
            }
            catch (Exception ex)
            {
                // General exception for write/read-write block
                if (lastPrintedWriteErrorCode != -2) // Use a general exception code
                {
                     Console.WriteLine($"{DateTime.Now}: 主循环操作中发生异常: {ex.Message}");
                     lastPrintedWriteErrorCode = -2;
                }
                isCurrentlyConnected = false; hasPrintedFailureMessageInThisDisconnectCycle = false;
                if (processHandle != IntPtr.Zero) { CloseHandle(processHandle); processHandle = IntPtr.Zero; }
                baseAddress = IntPtr.Zero; targetProcess = null;
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
        if (errorCode == -100) return "Partial read";
        if (errorCode == -300) return "Offset too large for 32-bit address space in debug read";
        if (errorCode == -301) return "Error calculating target address in debug read";
        return new System.ComponentModel.Win32Exception(errorCode).Message;
    }
}