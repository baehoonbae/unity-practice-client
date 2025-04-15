using System;
using System.Collections.Generic; // Dictionary 사용 위해 추가
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class HelloWorldScript : MonoBehaviour
{
    // --- Public Fields (Inspector에서 설정) ---
    public GameObject playerPrefab; // 다른 플레이어를 생성할 프리팹 (Inspector에서 PlayerSquare 프리팹 연결)

    // --- Private Fields ---
    private TcpClient client;
    private NetworkStream stream;
    private CancellationTokenSource cancellationTokenSource;

    private string myPlayerId = null; // 서버에서 받은 내 플레이어 ID
    private Dictionary<string, GameObject> otherPlayers = new Dictionary<string, GameObject>(); // 다른 플레이어들 관리 (Key: PlayerID, Value: GameObject)
    private Dictionary<string, Vector3> otherPlayersTargetPositions = new Dictionary<string, Vector3>(); // 다른 플레이어들의 목표 위치

    private Vector3 targetPosition; // 내 캐릭터의 목표 위치

    async void Start()
    {
        targetPosition = transform.position; // 내 캐릭터 시작 위치
        cancellationTokenSource = new CancellationTokenSource();

        // Player Prefab이 Inspector에서 할당되었는지 확인
        if (playerPrefab == null)
        {
            Debug.LogError("Player Prefab이 Inspector에 할당되지 않았습니다! 스크립트에 프리팹을 연결해주세요.");
            return; // 프리팹 없으면 실행 중단
        }

        await ConnectToServerAsync();

        if (client != null && client.Connected)
        {
            // 메시지 수신을 별도 Task로 실행 (메인 스레드 블로킹 방지)
            _ = ReceiveMessagesAsync(cancellationTokenSource.Token);
        }
    }

    async Task ConnectToServerAsync()
    {
        try
        {
            string serverIp = "127.0.0.1"; // 서버 IP 주소
            int port = 7777;            // 서버 포트 번호

            client = new TcpClient();
            Debug.Log($"서버에 연결 시도 중... ({serverIp}:{port})");
            await client.ConnectAsync(serverIp, port);
            Debug.Log("서버에 접속 성공!");

            stream = client.GetStream();
            Debug.Log("네트워크 스트림 열기 성공!");
        }
        catch (Exception e)
        {
            Debug.LogError($"서버 연결 실패: {e.Message}");
            CloseConnection();
        }
    }

    async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096]; // 버퍼 크기를 조금 늘림 (여러 메시지가 한번에 올 수 있음)
        StringBuilder messageBuilder = new StringBuilder(); // 메시지 조각을 합치기 위한 빌더

        try
        {
            Debug.Log("메시지 수신 시작...");
            while (client != null && client.Connected && !cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

                if (bytesRead == 0)
                {
                    Debug.Log("서버 연결 끊김 (bytesRead == 0).");
                    break;
                }

                // 받은 데이터를 문자열로 변환하고 빌더에 추가
                string receivedChunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                messageBuilder.Append(receivedChunk);

                // --- 여러 메시지 처리 (개행 문자로 구분) ---
                string allMessages = messageBuilder.ToString();
                int newlineIndex;
                // 개행 문자를 찾아서 메시지 단위로 처리
                while ((newlineIndex = allMessages.IndexOf('\n')) >= 0)
                {
                    string singleMessage = allMessages.Substring(0, newlineIndex).Trim(); // 개행 문자 앞까지 잘라내고 양쪽 공백 제거
                    allMessages = allMessages.Substring(newlineIndex + 1); // 처리한 메시지 이후 부분을 남김

                    if (!string.IsNullOrEmpty(singleMessage))
                    {
                         ProcessMessage(singleMessage); // 개별 메시지 처리 함수 호출
                    }
                }
                // 아직 처리 못한 메시지 조각은 빌더에 남겨둠
                messageBuilder.Clear();
                messageBuilder.Append(allMessages);


            }
        }
        catch (OperationCanceledException) { Debug.Log("메시지 수신 작업 취소됨."); }
        catch (IOException ioEx)           { Debug.LogError($"수신 중 네트워크 오류: {ioEx.Message}"); }
        catch (ObjectDisposedException)    { Debug.Log("수신 시도 중 연결/스트림이 이미 닫혔습니다."); }
        catch (Exception e)                { Debug.LogError($"메시지 수신 중 예외 발생: {e}"); }
        finally
        {
            Debug.Log("메시지 수신 루프 종료.");
            // 연결 종료는 OnDestroy에서 확실하게 처리
            // Unity 에디터 중지 시 비동기 작업이 이상하게 종료될 수 있으므로 주의
             if (Application.isPlaying) // 에디터 종료 시 호출 방지
             {
                 CloseConnection(); // 문제가 생기면 이 부분을 주석 처리하고 OnDestroy만 의존
             }
        }
    }

    // 수신된 개별 메시지를 처리하는 함수
    void ProcessMessage(string message)
    {
        Debug.Log($"서버 메시지 처리 시도: {message}");
        string[] parts = message.Split(',');

        if (parts.Length < 2) // 최소 명령어, ID는 있어야 함 (POS, LEAVE)
        {
            Debug.LogWarning($"처리할 수 없는 형식의 메시지: {message}");
            return;
        }

        string command = parts[0];
        string playerId = parts[1];

        // UI 업데이트 등 Unity API 호출은 메인 스레드에서 해야 함
        // 여기서는 일단 데이터만 처리하고, 실제 GameObject 조작은 Update에서 할 수도 있음
        // 하지만 Instantiate/Destroy는 여기서 직접 해도 큰 문제 없을 때가 많음

        try
        {
            if (command == "POS") // 위치 정보: "POS,id,x,y"
            {
                if (parts.Length == 4)
                {
                    int x = int.Parse(parts[2]);
                    int y = int.Parse(parts[3]);
                    Vector3 newPos = new Vector3(x, y, 0); // Z=0 가정 (2D 또는 TopDown View)

                    // 내 ID를 아직 모르면, 이 첫 POS 메시지가 내 정보일 가능성이 높음
                    if (myPlayerId == null)
                    {
                        myPlayerId = playerId;
                        targetPosition = newPos;
                        Debug.Log($"내 플레이어 ID 설정됨: {myPlayerId}, 초기 위치: {targetPosition}");
                        gameObject.name = $"Player_{myPlayerId}"; // 내 게임 오브젝트 이름 변경 (식별 용이)
                    }
                    else if (playerId == myPlayerId)
                    {
                        // 내 위치 업데이트
                        targetPosition = newPos;
                    }
                    else
                    {
                        // 다른 플레이어 위치 업데이트 (딕셔너리에 목표 위치 저장)
                        if (otherPlayers.ContainsKey(playerId))
                        {
                            otherPlayersTargetPositions[playerId] = newPos;
                        } else {
                             Debug.LogWarning($"위치 업데이트: ID {playerId} 플레이어를 찾을 수 없음 (아직 JOIN 안됨?). 메시지: {message}");
                             // JOIN이 POS보다 늦게 오는 경우 발생 가능 -> JOIN 시 처리하거나 여기서 임시 생성 고려
                        }
                    }
                } else Debug.LogWarning($"잘못된 POS 메시지 형식: {message}");
            }
            else if (command == "JOIN") // 새 플레이어 접속: "JOIN,id,x,y"
            {
                 if (parts.Length == 4 && playerId != myPlayerId && !otherPlayers.ContainsKey(playerId))
                 {
                    int x = int.Parse(parts[2]);
                    int y = int.Parse(parts[3]);
                    Vector3 startPos = new Vector3(x, y, 0); // Z=0 가정

                    Debug.Log($"다른 플레이어({playerId}) 접속. 위치: {startPos}");
                    // 다른 플레이어 프리팹 생성 (Instantiate)
                    GameObject newPlayerGO = Instantiate(playerPrefab, startPos, Quaternion.identity);
                    newPlayerGO.name = $"Player_{playerId}"; // 이름 설정 (식별 용이)
                    otherPlayers.Add(playerId, newPlayerGO);
                    otherPlayersTargetPositions.Add(playerId, startPos); // 초기 목표 위치 설정

                    // (선택사항) 생성된 플레이어에게 색상 등 구분 가능한 요소 추가
                 } else if(playerId == myPlayerId) {
                     // 서버가 내 JOIN 메시지를 나에게도 보내는 경우 (Broadcast 로직에 따라 다름), 무시
                 } else if(otherPlayers.ContainsKey(playerId)) {
                     Debug.LogWarning($"이미 존재하는 플레이어 ID({playerId})의 JOIN 메시지 수신. 무시.");
                 } else {
                     Debug.LogWarning($"잘못된 JOIN 메시지 형식 또는 내 ID: {message}");
                 }
            }
            else if (command == "LEAVE") // 플레이어 퇴장: "LEAVE,id"
            {
                 if (playerId != myPlayerId && otherPlayers.ContainsKey(playerId))
                 {
                     Debug.Log($"플레이어({playerId}) 퇴장.");
                     // 다른 플레이어 GameObject 제거 (Destroy)
                     Destroy(otherPlayers[playerId]);
                     otherPlayers.Remove(playerId);
                     otherPlayersTargetPositions.Remove(playerId);
                 } else if(playerId == myPlayerId) {
                      Debug.LogWarning("내가 LEAVE 메시지를 받음? 서버 로직 확인 필요.");
                 } else {
                      Debug.LogWarning($"퇴장 처리: ID {playerId} 플레이어를 찾을 수 없음. 메시지: {message}");
                 }
            }
            // 여기에 다른 명령어 처리 추가 가능 (예: CHAT, ATTACK 등)
        }
        catch (FormatException formatEx) { Debug.LogError($"메시지 파싱 오류 (Format): {message} - {formatEx.Message}"); }
        catch (Exception e)              { Debug.LogError($"메시지 처리 중 오류: {message} - {e}"); }
    }


    void Update()
    {
        // --- 내 캐릭터 이동 처리 ---
        if (client != null && client.Connected)
        {
            // 목표 위치로 부드럽게 이동 (Lerp)
            transform.position = Vector3.Lerp(transform.position, targetPosition, Time.deltaTime * 10.0f);

            // 키 입력 받아 서버로 전송
            SendMovementInput();
        }

        // --- 다른 플레이어들 이동 처리 ---
        // 메인 스레드에서 GameObject 위치 업데이트
         List<string> playerIds = new List<string>(otherPlayers.Keys); // 반복 중 변경될 수 있으므로 키 목록 복사
         foreach (string playerId in playerIds)
         {
             if (otherPlayers.TryGetValue(playerId, out GameObject playerGO) &&
                 otherPlayersTargetPositions.TryGetValue(playerId, out Vector3 targetPos))
             {
                 if (playerGO != null) // Destroy된 경우 대비
                 {
                     playerGO.transform.position = Vector3.Lerp(playerGO.transform.position, targetPos, Time.deltaTime * 10.0f);
                 }
                 else // GameObject가 파괴되었는데 딕셔너리에 남아있는 경우 (정리 필요)
                 {
                      Debug.LogWarning($"otherPlayers 딕셔너리에 파괴된 GameObject 참조가 남아있음: {playerId}. 정리 시도.");
                      otherPlayers.Remove(playerId);
                      otherPlayersTargetPositions.Remove(playerId);
                 }
             }
         }
    }

    void SendMovementInput()
    {
        // GetKeyDown: 키를 누르는 순간 딱 한번 true
        // GetKey: 키를 누르고 있는 동안 계속 true
        // 여기서는 GetKeyDown 사용 (한번 누르면 한번 메시지 전송)
        string inputMessage = null;
        if (Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.UpArrow)) inputMessage = "UP";
        else if (Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.DownArrow)) inputMessage = "DOWN";
        else if (Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.LeftArrow)) inputMessage = "LEFT";
        else if (Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.RightArrow)) inputMessage = "RIGHT";

        if (inputMessage != null && stream != null && client.Connected)
        {
            try
            {
                byte[] dataToSend = Encoding.UTF8.GetBytes(inputMessage + "\n"); // 서버가 개행 기준으로 읽도록 수정
                stream.WriteAsync(dataToSend, 0, dataToSend.Length); // 비동기 전송 사용 권장
            }
            catch (Exception e)
            {
                 Debug.LogError($"메시지 전송 실패: {e.Message}");
                 CloseConnection(); // 전송 실패 시 연결 종료 고려
            }
        }
    }

    // 연결 종료 및 리소스 정리
    void CloseConnection()
    {
        if (client == null) return; // 이미 닫혔으면 무시

        Debug.Log("연결 종료 및 리소스 정리 시작...");
        try { cancellationTokenSource?.Cancel(); } catch { /* Ignore */ }
        try { stream?.Close(); } catch { /* Ignore */ }
        try { client?.Close(); } catch { /* Ignore */ }

        stream = null;
        client = null;
        myPlayerId = null; // ID 초기화

        // 다른 플레이어 GameObject들 제거
        foreach (var playerGO in otherPlayers.Values)
        {
            if (playerGO != null) Destroy(playerGO);
        }
        otherPlayers.Clear();
        otherPlayersTargetPositions.Clear();

        Debug.Log("연결 종료 및 리소스 정리 완료.");
    }

    // 게임 오브젝트가 파괴될 때 호출됨 (씬 변경, 게임 종료 등)
    void OnDestroy()
    {
        Debug.Log("HelloWorldScript OnDestroy 호출됨.");
        CloseConnection();
        cancellationTokenSource?.Dispose(); // CancellationTokenSource 리소스 해제
    }

     // 에디터에서 플레이 중지 시 호출 (필요에 따라 사용)
     void OnApplicationQuit()
     {
         Debug.Log("애플리케이션 종료 중...");
         CloseConnection(); // 확실하게 종료
     }
}