using System;
using System.Collections.Generic;
using System.IO;
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

        private volatile bool _isRecording;
        private volatile bool isDisposed;
        private string currentFilePath;

        /// <summary>현재 녹화 진행 여부</summary>
        public bool IsRecording => _isRecording;

        /// <summary>최종 상태 메시지</summary>
        public string LastStatusMessage { get; private set; } = "";

        public event Action<string> RecordStatusChanged;

        public void WriteLog(string message, string filePath = "")
        {
            //try
            //{
            //    string logPath;
            //    if (string.IsNullOrEmpty(filePath))
            //    {
            //        string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VideoLogs");
            //        if (!Directory.Exists(logDir))
            //            Directory.CreateDirectory(logDir);
            //        logPath = Path.Combine(logDir, "recorder.log");
            //    }
            //    else
            //    {
            //        logPath = filePath;
            //    }

            //    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";

            //    // [FIX 8] 멀티스레드 환경에서 동시 파일 쓰기 충돌 방지
            //    lock (logLock)
            //    {
            //        File.AppendAllText(logPath, line);
            //    }
            //}
            //catch { }
        }

        public void StartRecording(int frameRate = 30, int bitRate = 8000000, string filePath = "", string dateFormet = "yyyyMMdd_HH")
        {
            // lock 밖에서 대기 — lock 내부에서 대기하면 30초간 모든 호출이 차단됨
            if (!completionSignal.Wait(TimeSpan.FromSeconds(30)))
            {
                NotifyStatus("이전 녹화 인코딩 대기 시간 초과. 녹화를 시작할 수 없습니다.");
                WriteLog("StartRecording: 이전 인코딩 대기 타임아웃");
                return;
            }

            lock (syncLock)
            {
                if (isDisposed)
                    throw new ObjectDisposedException(nameof(ScreenRecordManager));

                // 대기 사이에 다른 스레드가 녹사를 시작했을 수 있으므로 재확인
                if (_isRecording)
                {
                    NotifyStatus("이미 녹화가 진행 중입니다.");
                    return;
                }

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
                    // 디스플레이 없는 환경 방어
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

                    ReleaseRecorder();
                    completionSignal.Reset();

                    recorderInstance = Recorder.CreateRecorder(recorderOptions);
                    recorderInstance.OnRecordingComplete += HandleRecordingComplete;
                    recorderInstance.OnRecordingFailed += HandleRecordingFailed;
                    recorderInstance.OnStatusChanged += HandleInternalStatusChanged;

                    recorderInstance.Record(currentFilePath);
                    _isRecording = true;

                    // 이전 녹화 실패 시 남은 타이머가 새 녹화를 조기 중단하는 것을 방지
                    DisposeTimer();
                    autoStopTimer = new Timer(ExecuteTimeout, null, oneHourInMilliseconds, Timeout.Infinite);

                    NotifyStatus($"녹화 시작: {currentFilePath}");
                    WriteLog($"녹화 시작: {currentFilePath}");
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

                if (recorderInstance == null || !_isRecording)
                    return;

                try
                {
                    recorderInstance.Stop();
                    NotifyStatus("녹화 중지 요청됨. 인코딩 대기 중...");
                    WriteLog("녹화 중지 요청됨.");
                }
                catch (Exception ex)
                {
                    _isRecording = false;
                    completionSignal.Set();
                    NotifyStatus($"녹화 중지 실패: {ex.Message}");
                    WriteLog($"StopRecording 예외: {ex}");
                    return;
                }
            }

            if (!completionSignal.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                // 타임아웃 시 상태 강제 초기화
                _isRecording = false;
                completionSignal.Set();
                WriteLog("StopRecording: 인코딩 타임아웃. 상태 강제 초기화.");
                NotifyStatus("인코딩 완료 대기 시간 초과. 상태를 강제 초기화합니다.");

                lock (syncLock)
                {
                    ReleaseRecorder();
                }
            }
        }

        private void ExecuteTimeout(object stateInfo)
        {
            // ThreadPool 콜백 예외 방어 — 미처리 시 자동 중지 기능 영구 소실
            try
            {
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
            WriteLog($"[Recorder 내부 상태] {eventArgs.Status}");
        }

        private void HandleRecordingComplete(object sender, RecordingCompleteEventArgs eventArgs)
        {
            _isRecording = false;
            completionSignal.Set();

            try
            {
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
                WriteLog($"HandleRecordingComplete 예외: {ex}");
            }

            // 녹화 종료 후 타이머·Recorder 리소스 즉시 반환
            lock (syncLock)
            {
                DisposeTimer();
                ReleaseRecorder();
            }
        }

        private void HandleRecordingFailed(object sender, RecordingFailedEventArgs eventArgs)
        {
            _isRecording = false;
            completionSignal.Set();

            try
            {
                string message = $"녹화 실패: {eventArgs.Error}";
                NotifyStatus(message);
                WriteLog(message);
            }
            catch (Exception ex)
            {
                WriteLog($"HandleRecordingFailed 예외: {ex}");
            }

            // 실패 시에도 타이머·Recorder 즉시 정리
            lock (syncLock)
            {
                DisposeTimer();
                ReleaseRecorder();
            }
        }

        private void NotifyStatus(string message)
        {
            LastStatusMessage = message;
            // 구독자 예외가 내부 로직을 중단시키지 않도록 방어
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
            if (recorder != null)
            {
                try { recorder.OnRecordingComplete -= HandleRecordingComplete; } catch { }
                try { recorder.OnRecordingFailed -= HandleRecordingFailed; } catch { }
                try { recorder.OnStatusChanged -= HandleInternalStatusChanged; } catch { }
                try { recorder.Dispose(); }
                catch (Exception ex) { WriteLog($"Recorder.Dispose 예외: {ex.Message}"); }
            }
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
                try { completionSignal.Dispose(); } catch { }
            }
        }
    }
}