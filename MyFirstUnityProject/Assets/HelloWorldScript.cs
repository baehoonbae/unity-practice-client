using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;          
using System.Threading.Tasks;    
using UnityEngine;

public class HelloWorldScript : MonoBehaviour
{
    private TcpClient client;
    private NetworkStream stream;
    private CancellationTokenSource cancellationTokenSource;

    // 네모(플레이어)의 위치를 저장할 변수
    // Vector3는 Unity에서 위치나 방향을 나타내는 구조체 (x, y, z 값 가짐)
    private Vector3 targetPosition;
    
    async void Start()
    {
        // 시작할 때 현재 위치를 초기 targetPosition으로 설정
        targetPosition = transform.position;
        
        cancellationTokenSource = new CancellationTokenSource();
        await ConnectToServerAsync();

        if (client != null && client.Connected)
        {
            ReceiveMessagesAsync(cancellationTokenSource.Token);
        }
    }

    // 서버 연결 부분을 비동기 Task로 변경
    async Task ConnectToServerAsync()
    {
        try
        {
            string serverIp = "127.0.0.1";
            int port = 7777;

            client = new TcpClient(); // TcpClient 생성과 연결 분리
            Debug.Log("서버에 연결 시도 중...");
            await client.ConnectAsync(serverIp, port); // 비동기 연결 시도 (await)
            Debug.Log("서버에 접속 성공!");

            stream = client.GetStream();
            Debug.Log("네트워크 스트림 열기 성공!");
        }
        catch (SocketException e)
        {
            Debug.LogError($"SocketException: 서버 연결 실패! 서버 실행 확인. - {e}");
            client?.Close(); // 실패 시 정리
            client = null;   // 연결 실패 표시
        }
        catch (Exception e)
        {
            Debug.LogError($"Exception during connection: {e}");
            client?.Close();
            client = null;
        }
    }

    // 서버로부터 메시지를 계속 받는 비동기 Task
    async void ReceiveMessagesAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024];
        try
        {
            while (client.Connected) // 연결되어 있는 동안 계속 시도
            {
                // 취소 요청이 있으면 루프 종료
                if (cancellationToken.IsCancellationRequested)
                {
                     Debug.Log("메시지 수신 중단됨 (취소 요청).");
                     break;
                }

                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

                if (bytesRead == 0)
                {
                    Debug.Log("서버가 연결을 끊었습니다.");
                    break;
                }

                string receivedMessage = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Debug.Log($"서버로부터 받은 메시지: {receivedMessage}");
                
                // 받은 메시지가 위치 정보인지 확인하고 처리
                if (receivedMessage.StartsWith("POS,")) // 메시지가 "POS," 로 시작하는가?
                {
                    // "POS,x,y" 형식에서 x와 y 값을 추출
                    string[] parts = receivedMessage.Split(','); // 콤마(,)로 문자열 분리
                    if (parts.Length == 3) // "POS", "x", "y" 세 부분으로 잘 나뉘었는지 확인
                    {
                        try
                        {
                            // 문자열을 정수(int)로 변환 시도
                            int receivedX = int.Parse(parts[1]);
                            int receivedY = int.Parse(parts[2]);

                            // targetPosition 업데이트 (Z 좌표는 원래 값 유지)
                            // Unity의 2D 좌표계는 보통 XY 평면 사용
                            targetPosition = new Vector3(receivedX, receivedY, transform.position.z);
                            // Debug.Log($"목표 위치 업데이트: {targetPosition}"); // 확인용 로그
                        }
                        catch (FormatException formatEx)
                        {
                            Debug.LogError($"잘못된 위치 정보 형식 수신: {receivedMessage} - {formatEx.Message}");
                        }
                        catch (Exception parseEx) // 그 외 파싱 에러
                        {
                            Debug.LogError($"위치 정보 파싱 중 오류: {receivedMessage} - {parseEx.Message}");
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"잘못된 형식의 POS 메시지 수신: {receivedMessage}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            Debug.Log("메시지 수신 작업이 취소되었습니다.");
        }
        catch (IOException ioEx)
        {
             Debug.LogError($"수신 중 네트워크 오류: {ioEx.Message}");
        }
        catch (ObjectDisposedException)
        {
            Debug.Log("수신 시도 중 연결이 이미 닫혔습니다.");
        }
        catch (Exception e)
        {
            Debug.LogError($"메시지 수신 중 예외 발생: {e}");
        }
        finally
        {
            Debug.Log("메시지 수신 루프 종료.");
            // 연결 종료는 OnDestroy에서 처리하는 것이 더 안전할 수 있음
            // CloseConnection();
        }
    }


    void Update()
    {
        // 위치를 targetPosition으로 부드럽게 이동
        // Lerp 함수는 현재 위치에서 목표 위치까지 부드럽게 이동하는 효과를 줌
        // Time.deltaTime은 한 프레임 동안 걸린 시간, 이걸 곱해주면 프레임 속도에 관계없이 일정한 속도로 움직임
        // 10.0f는 이동 속도 (값을 조절해서 속도 변경 가능)
        if (client != null && client.Connected) // 연결되어 있을 때만 움직임
        {
            transform.position = Vector3.Lerp(transform.position, targetPosition, Time.deltaTime * 10.0f);
        }
        
        if (stream != null && client != null && client.Connected)
        {
            SendMovementInput();
        }
    }

    void SendMovementInput()
    {
        string inputMessage = null;
        if (Input.GetKeyDown(KeyCode.W)) inputMessage = "UP";
        else if (Input.GetKeyDown(KeyCode.S)) inputMessage = "DOWN";
        else if (Input.GetKeyDown(KeyCode.A)) inputMessage = "LEFT";
        else if (Input.GetKeyDown(KeyCode.D)) inputMessage = "RIGHT";

        if (inputMessage != null)
        {
            try
            {
                byte[] dataToSend = Encoding.UTF8.GetBytes(inputMessage);
                stream.Write(dataToSend, 0, dataToSend.Length);
                // Debug.Log($"서버로 보낸 입력: {inputMessage}"); // 너무 자주 찍히면 주석 처리
            }
            catch (IOException ioEx)        { Debug.LogError($"메시지 전송 실패 (IOException): {ioEx.Message}"); CloseConnection(); }
            catch (ObjectDisposedException) { Debug.LogWarning("메시지 전송 시도 중 연결이 이미 닫혔습니다."); }
            catch (Exception e)             { Debug.LogError($"메시지 전송 실패: {e}"); CloseConnection(); }
        }
    }

    void CloseConnection()
    {
        Debug.Log("연결 종료 시도...");
        cancellationTokenSource?.Cancel();
        stream?.Close();
        client?.Close();
        stream = null;
        client = null;
    }

    void OnDestroy()
    {
        CloseConnection();
    }
}