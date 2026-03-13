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

        private volatile bool _isRecording;
        private volatile bool isDisposed;
        private string currentFilePath;

        /// <summary>현재 녹화 진행 여부 (LabVIEW 폴링용)</summary>
        public bool IsRecording => _isRecording;

        /// <summary>최종 상태 메시지 (LabVIEW 폴링용)</summary>
        public string LastStatusMessage { get; private set; } = "";

        public event Action<string> RecordStatusChanged;

        public void WriteLog(string message, string filePath = "")
        {
            if (string.IsNullOrEmpty(filePath))
            {
                try
                {
                    string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VideoLogs");
                    if (!Directory.Exists(logDir))
                    {
                        Directory.CreateDirectory(logDir);
                    }
                    string logPath = Path.Combine(logDir, "recorder.log");
                    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                    File.AppendAllText(logPath, line);
                }
                catch { }
            }
            else
            {
                try
                {
                    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                    File.AppendAllText(filePath, line);
                }
                catch { }
            }
        }
        public void StartRecording(int frameRate = 30, int bitRate = 8000000, string filePath = "", string dateFormet = "yyyyMMdd_HH")
        {
            lock (syncLock)
            {
                if (isDisposed)
                    throw new ObjectDisposedException(nameof(ScreenRecordManager));

                if (_isRecording)
                {
                    NotifyStatus("이미 녹화가 진행 중입니다.");
                    return;
                }

                // 이전 녹화의 인코딩이 아직 끝나지 않았다면 완료 대기
                if (!completionSignal.Wait(TimeSpan.FromSeconds(30)))
                {
                    NotifyStatus("이전 녹화 인코딩 대기 시간 초과. 녹화를 시작할 수 없습니다.");
                    return;
                }

                if (string.IsNullOrEmpty(filePath))
                {
                    string baseDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VideoLogs");
                    if (!Directory.Exists(baseDirectory))
                    {
                        Directory.CreateDirectory(baseDirectory);
                    }
                    filePath = baseDirectory;
                }

                string timeStamp = DateTime.Now.ToString(dateFormet);
                currentFilePath = Path.Combine(filePath, $"{timeStamp}.mp4");

                try
                {
                    var recorderOptions = new RecorderOptions
                    {
                        SourceOptions = new SourceOptions
                        {
                            RecordingSources = new List<RecordingSourceBase>
                            {
                                Recorder.GetDisplays()[0]
                            }
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

                    // 기존 인스턴스 정리 후 새로 생성
                    ReleaseRecorder();
                    completionSignal.Reset();

                    recorderInstance = Recorder.CreateRecorder(recorderOptions);
                    recorderInstance.OnRecordingComplete += HandleRecordingComplete;
                    recorderInstance.OnRecordingFailed += HandleRecordingFailed;
                    recorderInstance.OnStatusChanged += HandleInternalStatusChanged;

                    recorderInstance.Record(currentFilePath);
                    _isRecording = true;

                    autoStopTimer = new Timer(ExecuteTimeout, null, oneHourInMilliseconds, Timeout.Infinite);

                    NotifyStatus($"녹화 시작: {filePath}");
                }
                catch (Exception ex)
                {
                    completionSignal.Set();
                    _isRecording = false;
                    NotifyStatus($"녹화 시작 실패: {ex.Message}");
                    //WriteLog($"StartRecording 예외: {ex}");
                }
            }
        }

        /// <summary>
        /// 녹화를 중지하고 파일 인코딩 완료까지 블로킹 대기합니다.
        /// LabVIEW에서 호출해도 파일이 온전히 저장된 뒤 리턴됩니다.
        /// </summary>
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
                }
                catch (Exception ex)
                {
                    _isRecording = false;
                    completionSignal.Set();
                    NotifyStatus($"녹화 중지 실패: {ex.Message}");
                    //WriteLog($"StopRecording 예외: {ex}");
                    return;
                }
            }

            // lock 밖에서 대기 — 콜백이 completionSignal.Set()을 호출해야 하므로
            if (!completionSignal.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                NotifyStatus("인코딩 완료 대기 시간이 초과되었습니다.");
                //WriteLog("StopRecording: 인코딩 타임아웃");
            }
        }

        private void ExecuteTimeout(object stateInfo)
        {
            StopRecording();
            NotifyStatus("1시간 제한으로 녹화가 자동 중지되었습니다.");
        }

        private void HandleInternalStatusChanged(object sender, RecordingStatusEventArgs eventArgs)
        {
            string detail = $"[Recorder 내부 상태] {eventArgs.Status}";
            //WriteLog(detail);
        }

        private void HandleRecordingComplete(object sender, RecordingCompleteEventArgs eventArgs)
        {
            _isRecording = false;
            completionSignal.Set();

            bool fileExists = File.Exists(eventArgs.FilePath);
            long fileSize = fileExists ? new FileInfo(eventArgs.FilePath).Length : 0;

            string message = fileExists
                ? $"성공적으로 저장되었습니다: {eventArgs.FilePath} ({fileSize / 1024} KB)"
                : $"완료 이벤트 수신했으나 파일이 존재하지 않습니다: {eventArgs.FilePath}";

            NotifyStatus(message);
            //WriteLog(message);

            if (isDisposed)
            {
                lock (syncLock) { ReleaseRecorder(); }
            }
        }

        private void HandleRecordingFailed(object sender, RecordingFailedEventArgs eventArgs)
        {
            _isRecording = false;
            completionSignal.Set();

            string message = $"녹화 실패: {eventArgs.Error}";
            NotifyStatus(message);
            //WriteLog(message);

            if (isDisposed)
            {
                lock (syncLock) { ReleaseRecorder(); }
            }
        }

        private void NotifyStatus(string message)
        {
            LastStatusMessage = message;
            RecordStatusChanged?.Invoke(message);
        }

        private void DisposeTimer()
        {
            if (autoStopTimer != null)
            {
                autoStopTimer.Change(Timeout.Infinite, Timeout.Infinite);
                autoStopTimer.Dispose();
                autoStopTimer = null;
            }
        }

        private void ReleaseRecorder()
        {
            if (recorderInstance != null)
            {
                recorderInstance.OnRecordingComplete -= HandleRecordingComplete;
                recorderInstance.OnRecordingFailed -= HandleRecordingFailed;
                recorderInstance.OnStatusChanged -= HandleInternalStatusChanged;
                recorderInstance.Dispose();
                recorderInstance = null;
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
                completionSignal.Dispose();
            }
        }
    }
}