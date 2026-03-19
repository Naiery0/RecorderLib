using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using ScreenRecorderLib;

namespace RecorderLib
{
    public class ScreenRecordManager : IDisposable
    {
        private Recorder recorderInstance;
        private Timer autoStopTimer;
        private readonly int oneHourInMilliseconds = 3600000;
        private readonly object syncLock = new object();
        private readonly ManualResetEventSlim completionSignal = new ManualResetEventSlim(true);
        private static readonly object logLock = new object();

        private readonly HashSet<Recorder> _disposedRecorders = new HashSet<Recorder>();

        private int _recordingGeneration;

        private volatile bool _isRecording;
        private volatile bool isDisposed;
        private volatile bool _isStopping;
        private string currentFilePath;

        /// <summary>현재 녹화 진행 여부</summary>
        public bool IsRecording => _isRecording;
        /// <summary>최종 상태 메시지</summary>
        public string LastStatusMessage { get; private set; } = "";

        public event Action<string> RecordStatusChanged;

        private string filePath;
        public void WriteLog(string message)
        {
            try
            {
                string logDir = string.IsNullOrEmpty(filePath)
                    ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VideoLogs")
                    : filePath;

                if (!Directory.Exists(logDir))
                    Directory.CreateDirectory(logDir);

                string logPath = Path.Combine(logDir, "recorder.log");
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";

                lock (logLock)
                {
                    File.AppendAllText(logPath, line);
                }
            }
            catch
            {
            }
        }

        ///// <summary>
        ///// 짧은 녹화를 반복하여 장시간 사이클의 리소스 누수 및 안정성을 검증합니다.
        ///// 1시간 녹화 × N회를 짧은 녹화(recordSeconds)로 시뮬레이션합니다.
        ///// </summary>
        ///// <param name="totalCycles">반복 횟수 (기본 100)</param>
        ///// <param name="recordSeconds">사이클당 녹화 시간 (기본 5초)</param>
        ///// <param name="onProgress">진행 콜백 (사이클 번호, 상태 메시지)</param>
        ///// <param name="isCancelled">취소 여부 확인 함수</param>
        //public BenchmarkResult RunBenchmark(
        //    int totalCycles = 100,
        //    int recordSeconds = 5,
        //    Action<int, string> onProgress = null,
        //    Func<bool> isCancelled = null)
        //{
        //    var result = new BenchmarkResult { TotalCycles = totalCycles };
        //    var process = Process.GetCurrentProcess();
        //    var sw = Stopwatch.StartNew();

        //    process.Refresh();
        //    long startMemory = process.WorkingSet64;
        //    int startHandles = process.HandleCount;
        //    result.PeakMemoryBytes = startMemory;
        //    result.PeakHandleCount = startHandles;

        //    long prevMemory = startMemory;
        //    int prevHandles = startHandles;

        //    string benchDir = Path.Combine(
        //        Path.GetTempPath(),
        //        "RecorderBenchmark_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));

        //    WriteLog($"═══ 벤치마크 시작: {totalCycles}사이클 × {recordSeconds}초 ═══");

        //    try
        //    {
        //        for (int i = 1; i <= totalCycles; i++)
        //        {
        //            if (isCancelled?.Invoke() == true)
        //            {
        //                WriteLog($"벤치마크 취소: {i - 1}/{totalCycles} 완료");
        //                break;
        //            }

        //            // ── 녹화 시작 (이전 녹화 자동 중지+분리) ──
        //            StartRecording(
        //                filePath: benchDir,
        //                dateFormet: "yyyyMMdd_HHmmss_fff");

        //            if (!_isRecording)
        //            {
        //                result.Errors.Add($"사이클 {i}: 시작 실패 — {LastStatusMessage}");
        //                Thread.Sleep(1000);
        //                continue;
        //            }

        //            Thread.Sleep(recordSeconds * 1000);

        //            // ── orphan 인코딩 완료 대기 (최대 10초) ──
        //            // 이전 사이클의 orphan이 SafeDisposeRecorder를 완료할 시간 확보
        //            Thread.Sleep(2000);

        //            // ── 주기적 GC (10사이클마다) ──
        //            // 프로덕션에서는 유휴→StartRecording의 else 분기에서 GC가 호출됨
        //            // 벤치마크에서는 orphan 경로만 타므로 수동 호출 필요
        //            if (i % 10 == 0)
        //            {
        //                GC.Collect();
        //                GC.WaitForPendingFinalizers();
        //                GC.Collect();

        //                // 완료된 벤치마크 파일 중간 정리 (파일 핸들 해제)
        //                CleanupBenchmarkFiles(benchDir);
        //            }

        //            // ── 리소스 모니터링 ──
        //            process.Refresh();
        //            long mem = process.WorkingSet64;
        //            int handles = process.HandleCount;
        //            long memDelta = mem - prevMemory;
        //            int handleDelta = handles - prevHandles;

        //            if (mem > result.PeakMemoryBytes) result.PeakMemoryBytes = mem;
        //            if (handles > result.PeakHandleCount) result.PeakHandleCount = handles;

        //            result.CompletedCycles = i;
        //            prevMemory = mem;
        //            prevHandles = handles;

        //            string progress =
        //                $"[{i}/{totalCycles}] " +
        //                $"메모리: {mem / 1024 / 1024} MB (시작 대비 {(mem - startMemory) / 1024 / 1024:+0;-0;0}, 전 사이클 대비 {memDelta / 1024 / 1024:+0;-0;0}) | " +
        //                $"핸들: {handles} (시작 대비 {handles - startHandles:+0;-0;0}, 전 사이클 대비 {handleDelta:+0;-0;0})";

        //            onProgress?.Invoke(i, progress);
        //            WriteLog(progress);
        //        }

        //        // ── 마지막 녹화 정상 종료 ──
        //        if (_isRecording || _isStopping)
        //        {
        //            StopRecording(30);
        //            WaitForRecordingComplete(30);
        //        }

        //        // ── 모든 orphan 인코딩 완료 대기 + 최종 GC ──
        //        Thread.Sleep(5000);
        //        GC.Collect();
        //        GC.WaitForPendingFinalizers();
        //        GC.Collect();
        //    }
        //    catch (Exception ex)
        //    {
        //        result.Errors.Add($"벤치마크 예외: {ex.Message}");
        //        WriteLog($"벤치마크 예외: {ex}");
        //    }
        //    finally
        //    {
        //        CleanupBenchmarkFiles(benchDir);
        //    }

        //    sw.Stop();
        //    process.Refresh();

        //    result.Elapsed = sw.Elapsed;
        //    result.MemoryDeltaBytes = process.WorkingSet64 - startMemory;
        //    result.HandleDelta = process.HandleCount - startHandles;

        //    WriteLog(result.Summary);
        //    return result;
        //}

        //private static void CleanupBenchmarkFiles(string directory)
        //{
        //    try
        //    {
        //        if (!Directory.Exists(directory)) return;
        //        foreach (var file in Directory.GetFiles(directory))
        //        {
        //            try { File.Delete(file); } catch { }
        //        }
        //        try { Directory.Delete(directory, true); } catch { }
        //    }
        //    catch { }
        //}

        public void StartRecording(int frameRate = 30, int bitRate = 8000000, string filePath = "", string dateFormet = "yyyyMMdd_HH")
        {
            this.filePath = filePath;

            lock (syncLock)
            {
                if (isDisposed)
                    throw new ObjectDisposedException(nameof(ScreenRecordManager));

                if (_isRecording && !_isStopping)
                {
                    WriteLog("StartRecording: 캡처 중 재녹화 — 자동 중지 및 분리");
                    try { recorderInstance.Stop(); } catch { }
                    OrphanCurrentRecorder();
                }
                else if (_isStopping)
                {
                    WriteLog("StartRecording: 인코딩 중 재녹화 — 이전 인스턴스 분리");
                    OrphanCurrentRecorder();
                }
                else
                {
                    DisposeTimer();
                    ReleaseRecorder();
                }

                // 모든 경로 공통: 이전 사이클의 COM/DXGI RCW 회수
                // LabVIEW에서 1시간마다 1회 호출이므로 성능 영향 없음
                GC.Collect();
                GC.WaitForPendingFinalizers();

                if (string.IsNullOrEmpty(filePath))
                {
                    string baseDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VideoLogs");
                    if (!Directory.Exists(baseDirectory))
                        Directory.CreateDirectory(baseDirectory);
                    filePath = baseDirectory;
                }

                string timeStamp = DateTime.Now.ToString(dateFormet);
                string baseFileName = $"{timeStamp}.mp4";
                currentFilePath = Path.Combine(filePath, baseFileName);

                int duplicateIndex = 1;
                while (File.Exists(currentFilePath))
                {
                    string numberedName = Path.GetFileNameWithoutExtension(baseFileName) + $" ({duplicateIndex})" + Path.GetExtension(baseFileName);
                    currentFilePath = Path.Combine(filePath, numberedName);
                    duplicateIndex++;
                }

                try
                {
                    var displays = Recorder.GetDisplays();
                    if (displays == null || displays.Count == 0)
                    {
                        NotifyStatus("녹화 시작 실패: 유효한 디스플레이를 찾을 수 없습니다.");
                        WriteLog("StartRecording: 디스플레이 없음");
                        return;
                    }

                    var recorderOptions = new RecorderOptions
                    {
                        SourceOptions = new SourceOptions
                        {
                            RecordingSources = new List<RecordingSourceBase> { displays[0] }
                        },
                        OutputOptions = new OutputOptions
                        {
                            RecorderMode = RecorderMode.Video
                        },
                        VideoEncoderOptions = new VideoEncoderOptions
                        {
                            Framerate = frameRate,
                            Bitrate = bitRate,
                            IsFixedFramerate = true,
                            Encoder = new H264VideoEncoder
                            {
                                BitrateMode = H264BitrateControlMode.CBR,
                                EncoderProfile = H264Profile.Main
                            }
                        }
                    };

                    completionSignal.Reset();
                    _isStopping = false;

                    recorderInstance = Recorder.CreateRecorder(recorderOptions);
                    recorderInstance.OnRecordingComplete += HandleRecordingComplete;
                    recorderInstance.OnRecordingFailed += HandleRecordingFailed;
                    recorderInstance.OnStatusChanged += HandleInternalStatusChanged;

                    recorderInstance.Record(currentFilePath);
                    _isRecording = true;

                    int currentGen = ++_recordingGeneration;
                    DisposeTimer();
                    autoStopTimer = new Timer(
                        state => ExecuteTimeout((int)state),
                        currentGen,
                        oneHourInMilliseconds,
                        Timeout.Infinite);

                    NotifyStatus($"녹화 시작: {currentFilePath}");
                    WriteLog($"녹화 시작 [세대 {currentGen}]: {currentFilePath}");
                }
                catch (Exception ex)
                {
                    completionSignal.Set();
                    _isRecording = false;
                    NotifyStatus($"녹화 시작 실패: {ex.Message}");
                    WriteLog($"StartRecording 예외: {ex}");
                }
            }
        }

        public void StopRecording(int timeoutSeconds = 60)
        {
            lock (syncLock)
            {
                DisposeTimer();

                if (recorderInstance == null || !_isRecording || _isStopping)
                    return;

                _isStopping = true;

                try
                {
                    recorderInstance.Stop();
                    NotifyStatus("녹화 중지 요청됨. 인코딩 대기 중...");
                    WriteLog("녹화 중지 요청됨.");
                }
                catch (Exception ex)
                {
                    _isRecording = false;
                    _isStopping = false;
                    completionSignal.Set();
                    NotifyStatus($"녹화 중지 실패: {ex.Message}");
                    WriteLog($"StopRecording 예외: {ex}");
                    return;
                }
            }

            if (!completionSignal.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                _isRecording = false;
                _isStopping = false;
                completionSignal.Set();
                WriteLog("StopRecording: 인코딩 타임아웃. 상태 강제 초기화.");
                NotifyStatus("인코딩 완료 대기 시간 초과. 상태를 강제 초기화합니다.");

                lock (syncLock)
                {
                    ReleaseRecorder();
                }
            }
        }

        private void ExecuteTimeout(int timerGeneration)
        {
            try
            {
                lock (syncLock)
                {
                    if (_recordingGeneration != timerGeneration)
                    {
                        WriteLog($"ExecuteTimeout: 만료된 타이머 무시 (타이머 세대={timerGeneration}, 현재={_recordingGeneration})");
                        return;
                    }
                }

                StopRecording();
                NotifyStatus("1시간 제한으로 녹화가 자동 중지되었습니다.");
                WriteLog("자동 타임아웃 녹화 중지.");
            }
            catch (Exception ex)
            {
                WriteLog($"ExecuteTimeout 예외: {ex}");
            }
        }

        private void HandleInternalStatusChanged(object sender, RecordingStatusEventArgs eventArgs)
        {
            try
            {
                WriteLog($"[Recorder 내부 상태] {eventArgs.Status}");
            }
            catch (Exception ex)
            {
                WriteLog($"HandleInternalStatusChanged 예외: {ex.Message}");
            }
        }

        private void HandleRecordingComplete(object sender, RecordingCompleteEventArgs eventArgs)
        {
            try
            {
                _isRecording = false;
                _isStopping = false;
                completionSignal.Set();

                bool fileExists = File.Exists(eventArgs.FilePath);
                long fileSize = fileExists ? new FileInfo(eventArgs.FilePath).Length : 0;

                string message = fileExists
                    ? $"성공적으로 저장되었습니다: {eventArgs.FilePath} ({fileSize / 1024} KB)"
                    : $"완료 이벤트 수신했으나 파일이 존재하지 않습니다: {eventArgs.FilePath}";

                NotifyStatus(message);
                WriteLog(message);
            }
            catch (Exception ex)
            {
                _isRecording = false;
                _isStopping = false;
                WriteLog($"HandleRecordingComplete 예외: {ex}");
            }

            ScheduleRecorderCleanup(sender as Recorder);
        }

        private void HandleRecordingFailed(object sender, RecordingFailedEventArgs eventArgs)
        {
            try
            {
                _isRecording = false;
                _isStopping = false;
                completionSignal.Set();

                string message = $"녹화 실패: {eventArgs.Error}";
                NotifyStatus(message);
                WriteLog(message);
            }
            catch (Exception ex)
            {
                _isRecording = false;
                _isStopping = false;
                WriteLog($"HandleRecordingFailed 예외: {ex}");
            }

            ScheduleRecorderCleanup(sender as Recorder);
        }

        private void SafeDisposeRecorder(Recorder recorder)
        {
            if (recorder == null)
                return;

            if (_disposedRecorders.Contains(recorder))
                return;

            _disposedRecorders.Add(recorder);

            try { recorder.OnRecordingComplete -= HandleRecordingComplete; } catch { }
            try { recorder.OnRecordingFailed -= HandleRecordingFailed; } catch { }
            try { recorder.OnStatusChanged -= HandleInternalStatusChanged; } catch { }
            try { recorder.Dispose(); }
            catch (Exception ex) { WriteLog($"Recorder.Dispose 예외: {ex.Message}"); }

            if (_disposedRecorders.Count > 10)
                _disposedRecorders.Clear();
        }

        private void ScheduleRecorderCleanup(Recorder completedRecorder)
        {
            if (completedRecorder == null)
                return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    lock (syncLock)
                    {
                        if (ReferenceEquals(recorderInstance, completedRecorder))
                        {
                            recorderInstance = null;
                        }
                        SafeDisposeRecorder(completedRecorder);
                    }
                }
                catch (Exception ex)
                {
                    WriteLog($"ScheduleRecorderCleanup 예외: {ex.Message}");
                }
            });
        }

        private void NotifyStatus(string message)
        {
            LastStatusMessage = message;
            try
            {
                RecordStatusChanged?.Invoke(message);
            }
            catch (Exception ex)
            {
                WriteLog($"RecordStatusChanged 구독자 예외: {ex.Message}");
            }
        }

        private void DisposeTimer()
        {
            var timer = autoStopTimer;
            autoStopTimer = null;
            if (timer != null)
            {
                try
                {
                    timer.Change(Timeout.Infinite, Timeout.Infinite);
                    timer.Dispose();
                }
                catch { }
            }
        }

        private void ReleaseRecorder()
        {
            var recorder = recorderInstance;
            recorderInstance = null;
            SafeDisposeRecorder(recorder);
        }

        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;

            if (_isRecording)
            {
                StopRecording();
            }

            lock (syncLock)
            {
                DisposeTimer();
                ReleaseRecorder();
                Thread.Sleep(200);
                try { completionSignal.Dispose(); } catch { }
                _disposedRecorders.Clear();
            }
        }

        public bool WaitForRecordingComplete(int timeoutSeconds = 3660)
        {
            if (!_isRecording && !_isStopping)
                return true;

            return completionSignal.Wait(TimeSpan.FromSeconds(timeoutSeconds));
        }

        private void OrphanCurrentRecorder()
        {
            var orphan = recorderInstance;
            recorderInstance = null;

            _isRecording = false;
            _isStopping = false;
            completionSignal.Set();
            DisposeTimer();

            if (orphan == null)
                return;

            EventHandler<RecordingCompleteEventArgs> cleanupComplete = (s, e) =>
            {
                try
                {
                    long fileSize = 0;
                    try { fileSize = new FileInfo(e.FilePath).Length / 1024; } catch { }
                    WriteLog($"[분리된 인코딩 완료] {e.FilePath} ({fileSize} KB)");
                }
                catch { }

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        lock (syncLock)
                        {
                            SafeDisposeRecorder(s as Recorder);
                        }
                    }
                    catch (Exception ex) { WriteLog($"분리 인스턴스 Dispose 예외: {ex.Message}"); }
                });
            };
            EventHandler<RecordingFailedEventArgs> cleanupFailed = (s, e) =>
            {
                try { WriteLog($"[분리된 인코딩 실패] {e.Error}"); } catch { }

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        lock (syncLock)
                        {
                            SafeDisposeRecorder(s as Recorder);
                        }
                    }
                    catch (Exception ex) { WriteLog($"분리 인스턴스 Dispose 예외: {ex.Message}"); }
                });
            };

            orphan.OnRecordingComplete += cleanupComplete;
            orphan.OnRecordingFailed += cleanupFailed;
            orphan.OnRecordingComplete -= HandleRecordingComplete;
            orphan.OnRecordingFailed -= HandleRecordingFailed;
            try { orphan.OnStatusChanged -= HandleInternalStatusChanged; } catch { }

            WriteLog("이전 Recorder 분리 완료 — 백그라운드 인코딩 계속");
        }
    }
}