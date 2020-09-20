Imports System
Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading

Module modBroadcast

    Friend Const gLocalBroadCast As String = "127.0.0.1" ' System.Net.IPAddress.Loopback.ToString
    Friend Const gNetworkBroadCast As String = "255.255.255.255" ' System.Net.IPAddress.Broadcast.ToString

    Friend Class Broadcast

        Private Shared UDP_Client1 As New UdpClient
        Private Shared UDP_Server_Port1 As Integer
        Private Shared thdUdp1 As Thread
        Private Shared UDP_Server1 As UdpClient
        Public Shared Event DataArrival1(ByVal Data As String)
        Public Shared Event Sock_Error1(ByVal Description As String)

        Private Shared objlockA As Object = New Object
        Friend Shared Sub UDP_Send1(ByVal Host As String, ByVal Port As Integer, ByVal Data As String)

            SyncLock objlockA
                Try
                    UDP_Client1.Connect(Host, Port)
                    Dim sendBytes As [Byte]() = Encoding.ASCII.GetBytes(Data)
                    UDP_Client1.Send(sendBytes, sendBytes.Length)

                Catch ex As Exception
                End Try
            End SyncLock

        End Sub
        Friend Shared Sub UDP_Listen1(ByVal Port As Integer)

            Try
                UDP_Server_Port1 = Port
                UDP_Server1 = New UdpClient(Port)
                thdUdp1 = New Thread(AddressOf GetUDPData1)
                thdUdp1.Start()

            Catch ex1 As System.Net.Sockets.SocketException
                Dim a = 1

            Catch ex As Exception

            End Try
        End Sub

        Private Shared Sub GetUDPData1()
            Do While True
                Try
                    Dim RemoteIpEndPoint As New IPEndPoint(IPAddress.Any, 0)
                    Dim RData As String = Encoding.ASCII.GetString(UDP_Server1.Receive(RemoteIpEndPoint))
                    If RData = "Close me" Then Exit Do
                    RaiseEvent DataArrival1(RData)
                    Thread.Sleep(0)
                Catch ex As Exception
                    ' Debughandler(gFail &  ex.TargetSite.Name & " ) " & vbcrlf  & ex.Message)
                End Try
            Loop
        End Sub
        Friend Shared Sub CloseSock1()

            Try

                UDP_Send1("127.0.0.1", UDP_Server_Port1, "Close me")
                Thread.Sleep(300)
                UDP_Server1.Close()
                thdUdp1.Abort()
                Thread.Sleep(300)

            Catch ex As Exception

            End Try

        End Sub

        '***************************************************************************************************************************************************


    End Class

End Module
