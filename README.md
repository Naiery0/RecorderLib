# ScreenRecordManager

화면 녹화를 관리하는 클래스입니다.  
녹화 시작, 중지, 로그 작성 기능과 녹화 상태 확인 기능을 제공합니다.

---

# Class

`ScreenRecordManager`

---

# Methods

## 1. StartRecording

**1시간 녹화를 시작합니다.**

### Method Signature
```
StartRecording(
    int frameRate = 30,
    int bitRate = 8000000,
    string filePath = "",
    string dateFormet = "yyyyMMdd_HH"
)
```

### Parameters

| Parameter | Type | Default | Description |
|---|---|---|---|
| frameRate | int | 30 | 영상 프레임 레이트 |
| bitRate | int | 8000000 | 영상 비트레이트 |
| filePath | string | "" | 영상 저장 경로 |
| dateFormet | string | "yyyyMMdd_HH" | 파일명에 사용할 날짜 포맷 |

### Behavior

- 파라미터를 입력하지 않으면 **기본값으로 동작**
- `filePath` 미지정 시
  - 실행 경로에 **VideoLogs 폴더 생성**
  - 해당 폴더에 영상 저장
- 녹화 시간은 **최대 1시간**

---

## 2. StopRecording

**녹화를 중지합니다.**

### Method Signature
```
StopRecording(int timeoutSeconds = 60)
```

### Parameters

| Parameter | Type | Default | Description |
|---|---|---|---|
| timeoutSeconds | int | 60 | 인코딩 완료를 기다리는 최대 시간 (초) |

### Behavior

- 녹화 중지 요청 후 **인코딩 완료까지 대기**
- 지정된 시간 초과 시 **타임아웃 처리**

---

## 3. WriteLog

**녹화 관련 로그를 기록합니다.**

### Method Signature
```
WriteLog(string message, string filePath = "")
```

### Parameters

| Parameter | Type | Default | Description |
|---|---|---|---|
| message | string | - | 로그 메시지 |
| filePath | string | "" | 로그 파일 저장 경로 |

### Behavior

- 기본 경로는 **StartRecording의 filePath와 동일**
- 로그 형식

```
[yyyy-MM-dd HH:mm:ss.fff] message
```

---

# Properties

| Property | Type | Description |
|---|---|---|
| IsRecording | bool | 현재 녹화 진행 여부 |
| LastStatusMessage | string | 마지막 녹화 상태 메시지 |

---

# Status Messages

녹화 상태 메시지는 아래와 같은 형태로 전달됩니다.

```
NotifyStatus("이미 녹화가 진행 중입니다.");
NotifyStatus("이전 녹화 인코딩 대기 시간 초과. 녹화를 시작할 수 없습니다.");
NotifyStatus($"녹화 시작: {filePath}");
NotifyStatus($"녹화 시작 실패: {ex.Message}");
NotifyStatus("녹화 중지 요청됨. 인코딩 대기 중...");
NotifyStatus($"녹화 중지 실패: {ex.Message}");
NotifyStatus("인코딩 완료 대기 시간이 초과되었습니다.");
NotifyStatus("1시간 제한으로 녹화가 자동 중지되었습니다.");
NotifyStatus($"녹화 실패: {eventArgs.Error}");
```